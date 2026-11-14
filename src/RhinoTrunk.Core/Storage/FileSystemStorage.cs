using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using RhinoTrunk.Contracts;

namespace RhinoTrunk.Core.Storage;

/// <summary>
/// Simple durable storage in a directory tree, git-like and easy to inspect:
/// <code>
/// &lt;root&gt;/&lt;repo&gt;/repo.json
///                 objects/ab/cdef...        content-addressed objects
///                 refs.json                 { "refs/heads/main": "sha256:..." }
///                 reflog.jsonl              append-only ref history
///                 annotations/&lt;hex&gt;.jsonl  per commit
///                 derived/&lt;hex&gt;/&lt;recipe&gt;.json
/// </code>
/// Ref updates are serialized per repository inside one process. Running several server
/// processes against the same directory is not supported (see docs/OPEN_QUESTIONS.md).
/// </summary>
public sealed class FileSystemRepositoryProvider : IRepositoryProvider
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, FileSystemRepositoryStore> _open = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _createGate = new(1, 1);

    public FileSystemRepositoryProvider(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<IReadOnlyList<IRepositoryStore>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<IRepositoryStore>();
        foreach (var dir in Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (await GetAsync(name, ct) is { } repo) result.Add(repo);
        }
        return result;
    }

    public async Task<IRepositoryStore?> GetAsync(string name, CancellationToken ct = default)
    {
        if (!Names.IsValidRepo(name)) return null;
        if (_open.TryGetValue(name, out var cached)) return cached;
        var dir = Path.Combine(_root, name);
        var metaPath = Path.Combine(dir, "repo.json");
        if (!File.Exists(metaPath)) return null;
        var meta = JsonSerializer.Deserialize<RepoInfo>(await File.ReadAllBytesAsync(metaPath, ct), RhinoTrunkJson.Options)!;
        return _open.GetOrAdd(name, _ => new FileSystemRepositoryStore(dir, meta.Name, meta.Created));
    }

    public async Task<IRepositoryStore?> CreateAsync(string name, DateTimeOffset created, CancellationToken ct = default)
    {
        await _createGate.WaitAsync(ct);
        try
        {
            var dir = Path.Combine(_root, name);
            var metaPath = Path.Combine(dir, "repo.json");
            if (File.Exists(metaPath)) return null;
            Directory.CreateDirectory(Path.Combine(dir, "objects"));
            var info = new RepoInfo { Name = name, Created = created };
            await FileUtil.WriteAtomicAsync(metaPath, JsonSerializer.SerializeToUtf8Bytes(info, RhinoTrunkJson.Options), ct);
            return _open.GetOrAdd(name, _ => new FileSystemRepositoryStore(dir, name, created));
        }
        finally
        {
            _createGate.Release();
        }
    }
}

public sealed class FileSystemRepositoryStore : IRepositoryStore
{
    public FileSystemRepositoryStore(string dir, string name, DateTimeOffset created)
    {
        Name = name;
        Created = created;
        Objects = new FileSystemObjectStore(Path.Combine(dir, "objects"));
        Refs = new FileSystemRefStore(Path.Combine(dir, "refs.json"), Path.Combine(dir, "reflog.jsonl"));
        Annotations = new FileSystemAnnotationStore(Path.Combine(dir, "annotations"));
        Derived = new FileSystemDerivedStore(Path.Combine(dir, "derived"));
    }

    public string Name { get; }
    public DateTimeOffset Created { get; }
    public IObjectStore Objects { get; }
    public IRefStore Refs { get; }
    public IAnnotationStore Annotations { get; }
    public IDerivedStore Derived { get; }
}

internal static class FileUtil
{
    public static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, data.ToArray(), ct);
        File.Move(temp, path, overwrite: true);
    }

    public static string PathFor(string dir, string hash)
    {
        var hex = ContentHash.Hex(hash);
        return Path.Combine(dir, hex[..2], hex[2..]);
    }
}

public sealed class FileSystemObjectStore(string dir) : IObjectStore
{
    public Task<bool> ExistsAsync(string hash, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(FileUtil.PathFor(dir, hash)));

    public async Task<byte[]?> GetAsync(string hash, CancellationToken ct = default)
    {
        var path = FileUtil.PathFor(dir, hash);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    public async Task<bool> PutAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var path = FileUtil.PathFor(dir, hash);
        if (File.Exists(path)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, data.ToArray(), ct);
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp); // a concurrent writer stored the same content
            return false;
        }
    }
}

public sealed class FileSystemRefStore(string refsPath, string logPath) : IRefStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(refsPath)) return new Dictionary<string, string>(StringComparer.Ordinal);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllBytesAsync(refsPath, ct), RhinoTrunkJson.Options);
        return new Dictionary<string, string>(map ?? [], StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, string>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await LoadAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetAsync(string fullName, CancellationToken ct = default) =>
        (await ListAsync(ct)).TryGetValue(fullName, out var v) ? v : null;

    public async Task<bool> CompareAndSwapAsync(string fullName, string? expected, string? newValue, RefLogEntry log, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var refs = await LoadAsync(ct);
            refs.TryGetValue(fullName, out var current);
            if (!string.Equals(current, expected, StringComparison.Ordinal)) return false;
            if (newValue is null) refs.Remove(fullName);
            else refs[fullName] = newValue;
            var sorted = new SortedDictionary<string, string>(refs, StringComparer.Ordinal);
            await FileUtil.WriteAtomicAsync(refsPath, JsonSerializer.SerializeToUtf8Bytes(sorted, new JsonSerializerOptions { WriteIndented = true }), ct);
            await File.AppendAllTextAsync(logPath, JsonSerializer.Serialize(log, RhinoTrunkJson.Options) + "\n", Encoding.UTF8, ct);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RefLogEntry>> ReadLogAsync(CancellationToken ct = default)
    {
        if (!File.Exists(logPath)) return [];
        var lines = await File.ReadAllLinesAsync(logPath, ct);
        return lines.Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<RefLogEntry>(l, RhinoTrunkJson.Options)!).ToList();
    }
}

public sealed class FileSystemAnnotationStore(string dir) : IAnnotationStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AppendAsync(string commit, Annotation annotation, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ContentHash.Hex(commit) + ".jsonl");
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(annotation, RhinoTrunkJson.Options) + "\n", Encoding.UTF8, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Annotation>> ListAsync(string commit, CancellationToken ct = default)
    {
        var path = Path.Combine(dir, ContentHash.Hex(commit) + ".jsonl");
        if (!File.Exists(path)) return [];
        var lines = await File.ReadAllLinesAsync(path, ct);
        return lines.Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<Annotation>(l, RhinoTrunkJson.Options)!).ToList();
    }
}

public sealed class FileSystemDerivedStore(string dir) : IDerivedStore
{
    public Task SetAsync(DerivedRecord record, CancellationToken ct = default) =>
        FileUtil.WriteAtomicAsync(PathFor(record.Source, record.Recipe), JsonSerializer.SerializeToUtf8Bytes(record, RhinoTrunkJson.Options), ct);

    public async Task<DerivedRecord?> GetAsync(string source, string recipe, CancellationToken ct = default)
    {
        var path = PathFor(source, recipe);
        return File.Exists(path) ? JsonSerializer.Deserialize<DerivedRecord>(await File.ReadAllBytesAsync(path, ct), RhinoTrunkJson.Options) : null;
    }

    private string PathFor(string source, string recipe) => Path.Combine(dir, ContentHash.Hex(source), recipe + ".json");
}
