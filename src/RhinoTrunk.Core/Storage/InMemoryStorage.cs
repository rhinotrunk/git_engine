using System.Collections.Concurrent;
using RhinoTrunk.Contracts;

namespace RhinoTrunk.Core.Storage;

/// <summary>Volatile storage for tests and quick experiments.</summary>
public sealed class InMemoryRepositoryProvider : IRepositoryProvider
{
    private readonly ConcurrentDictionary<string, InMemoryRepositoryStore> _repos = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<IRepositoryStore>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IRepositoryStore>>(_repos.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToList());

    public Task<IRepositoryStore?> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<IRepositoryStore?>(_repos.TryGetValue(name, out var repo) ? repo : null);

    public Task<IRepositoryStore?> CreateAsync(string name, DateTimeOffset created, CancellationToken ct = default)
    {
        var repo = new InMemoryRepositoryStore(name, created);
        return Task.FromResult<IRepositoryStore?>(_repos.TryAdd(name, repo) ? repo : null);
    }
}

public sealed class InMemoryRepositoryStore(string name, DateTimeOffset created) : IRepositoryStore
{
    public string Name { get; } = name;
    public DateTimeOffset Created { get; } = created;
    public IObjectStore Objects { get; } = new InMemoryObjectStore();
    public IRefStore Refs { get; } = new InMemoryRefStore();
    public IAnnotationStore Annotations { get; } = new InMemoryAnnotationStore();
    public IDerivedStore Derived { get; } = new InMemoryDerivedStore();
}

public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public int Count => _objects.Count;

    public Task<bool> ExistsAsync(string hash, CancellationToken ct = default) => Task.FromResult(_objects.ContainsKey(hash));

    public Task<byte[]?> GetAsync(string hash, CancellationToken ct = default) =>
        Task.FromResult(_objects.TryGetValue(hash, out var data) ? data : null);

    public Task<bool> PutAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        Task.FromResult(_objects.TryAdd(hash, data.ToArray()));
}

public sealed class InMemoryRefStore : IRefStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _refs = new(StringComparer.Ordinal);
    private readonly List<RefLogEntry> _log = [];

    public Task<IReadOnlyDictionary<string, string>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(_refs, StringComparer.Ordinal));
    }

    public Task<string?> GetAsync(string fullName, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_refs.TryGetValue(fullName, out var v) ? v : null);
    }

    public Task<bool> CompareAndSwapAsync(string fullName, string? expected, string? newValue, RefLogEntry log, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _refs.TryGetValue(fullName, out var current);
            if (!string.Equals(current, expected, StringComparison.Ordinal)) return Task.FromResult(false);
            if (newValue is null) _refs.Remove(fullName);
            else _refs[fullName] = newValue;
            _log.Add(log);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<RefLogEntry>> ReadLogAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<RefLogEntry>>(_log.ToList());
    }
}

public sealed class InMemoryAnnotationStore : IAnnotationStore
{
    private readonly ConcurrentDictionary<string, List<Annotation>> _items = new(StringComparer.Ordinal);

    public Task AppendAsync(string commit, Annotation annotation, CancellationToken ct = default)
    {
        var list = _items.GetOrAdd(commit, _ => []);
        lock (list) list.Add(annotation);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Annotation>> ListAsync(string commit, CancellationToken ct = default)
    {
        if (!_items.TryGetValue(commit, out var list)) return Task.FromResult<IReadOnlyList<Annotation>>([]);
        lock (list) return Task.FromResult<IReadOnlyList<Annotation>>(list.ToList());
    }
}

public sealed class InMemoryDerivedStore : IDerivedStore
{
    private readonly ConcurrentDictionary<(string, string), DerivedRecord> _items = new();

    public Task SetAsync(DerivedRecord record, CancellationToken ct = default)
    {
        _items[(record.Source, record.Recipe)] = record;
        return Task.CompletedTask;
    }

    public Task<DerivedRecord?> GetAsync(string source, string recipe, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue((source, recipe), out var r) ? r : null);
}
