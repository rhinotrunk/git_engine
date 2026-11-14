using System.Collections.Concurrent;
using RhinoTrunk.Contracts;
using RhinoTrunk.Core.Storage;

namespace RhinoTrunk.Core;

/// <summary>
/// The versioning engine behind the REST API and CLI. All writes go through here, so every
/// commit and merge is validated and every ref update is a compare-and-swap.
/// </summary>
public sealed partial class RhinoTrunkService(IRepositoryProvider provider, TimeProvider? timeProvider = null)
{
    private const string HeadsPrefix = "refs/heads/";
    private const string TagsPrefix = "refs/tags/";
    public const int MaxBatch = 1000;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, RepoContext> _contexts = new(StringComparer.Ordinal);

    private DateTimeOffset Now()
    {
        var t = _time.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }

    // =========================================================================================
    // Repositories
    // =========================================================================================

    public async Task<RepoInfo> CreateRepoAsync(string name, CancellationToken ct = default)
    {
        if (!Names.IsValidRepo(name))
            throw RhinoTrunkException.BadRequest("Repository names use lowercase letters, digits, '.', '_' and '-' (1-64 characters).");
        var repo = await provider.CreateAsync(name, Now(), ct)
                   ?? throw RhinoTrunkException.Conflict(ErrorCodes.RepoExists, $"Repository '{name}' already exists.");
        return new RepoInfo { Name = repo.Name, Created = repo.Created };
    }

    public async Task<ReposResponse> ListReposAsync(CancellationToken ct = default) =>
        new() { Repos = (await provider.ListAsync(ct)).Select(r => new RepoInfo { Name = r.Name, Created = r.Created }).ToList() };

    public async Task<RepoInfo> GetRepoAsync(string name, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(name, ct);
        return new RepoInfo { Name = ctx.Store.Name, Created = ctx.Store.Created };
    }

    private async Task<RepoContext> RepoAsync(string name, CancellationToken ct)
    {
        var store = await provider.GetAsync(name, ct) ?? throw RhinoTrunkException.NotFound($"Repository '{name}' not found.");
        return _contexts.GetOrAdd(name, _ => new RepoContext(store));
    }

    // =========================================================================================
    // Objects
    // =========================================================================================

    public async Task<PutObjectResponse> PutObjectAsync(string repo, string hash, byte[] data, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        if (!ContentHash.IsValid(hash))
            throw RhinoTrunkException.BadRequest("Invalid hash; expected 'sha256:' followed by 64 lowercase hex digits.");
        var actual = ContentHash.Compute(data);
        if (actual != hash)
            throw new RhinoTrunkException(400, ErrorCodes.HashMismatch, $"Uploaded bytes hash to {actual}, not {hash}.", data: new() { ["actual"] = actual });
        var created = await ctx.Store.Objects.PutAsync(hash, data, ct);
        return new PutObjectResponse { Hash = hash, Created = created, Size = data.LongLength };
    }

    public async Task<byte[]> GetObjectAsync(string repo, string hash, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        if (!ContentHash.IsValid(hash)) throw RhinoTrunkException.BadRequest("Invalid hash.");
        return await ctx.Store.Objects.GetAsync(hash, ct) ?? throw RhinoTrunkException.NotFound($"Object {hash} not found.");
    }

    public async Task<MissingResponse> MissingAsync(string repo, HashesRequest request, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        CheckHashes(request.Hashes);
        var missing = new List<string>();
        foreach (var hash in request.Hashes.Distinct(StringComparer.Ordinal))
            if (!await ctx.Store.Objects.ExistsAsync(hash, ct)) missing.Add(hash);
        return new MissingResponse { Missing = missing };
    }

    public async Task<BatchGetResponse> BatchGetAsync(string repo, HashesRequest request, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        CheckHashes(request.Hashes);
        var result = new BatchGetResponse();
        foreach (var hash in request.Hashes.Distinct(StringComparer.Ordinal))
        {
            var bytes = await ctx.Store.Objects.GetAsync(hash, ct);
            if (bytes is null) result.Missing.Add(hash);
            else result.Objects[hash] = Convert.ToBase64String(bytes);
        }
        return result;
    }

    private static void CheckHashes(List<string> hashes)
    {
        if (hashes.Count > MaxBatch) throw RhinoTrunkException.BadRequest($"At most {MaxBatch} hashes per request.");
        var bad = hashes.Where(h => !ContentHash.IsValid(h)).Take(5).ToList();
        if (bad.Count > 0) throw RhinoTrunkException.BadRequest("Invalid hash: " + string.Join(", ", bad));
    }

    // =========================================================================================
    // Refs
    // =========================================================================================

    private static string FullRef(string type, string name) => (type == RefTypes.Tag ? TagsPrefix : HeadsPrefix) + name;

    private static RefInfo ToRefInfo(string fullName, string commit) => fullName.StartsWith(HeadsPrefix, StringComparison.Ordinal)
        ? new RefInfo { Name = fullName[HeadsPrefix.Length..], Type = RefTypes.Branch, Commit = commit }
        : new RefInfo { Name = fullName[TagsPrefix.Length..], Type = RefTypes.Tag, Commit = commit };

    private static void CheckRef(string type, string name)
    {
        if (type != RefTypes.Branch && type != RefTypes.Tag) throw RhinoTrunkException.BadRequest("Ref type must be 'branch' or 'tag'.");
        if (!Names.IsValidRefName(name)) throw RhinoTrunkException.BadRequest($"Invalid ref name '{name}'.");
    }

    public async Task<RefsResponse> ListRefsAsync(string repo, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var refs = await ctx.Store.Refs.ListAsync(ct);
        return new RefsResponse
        {
            Refs = refs.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => ToRefInfo(r.Key, r.Value)).ToList(),
        };
    }

    public async Task<RefInfo> GetRefAsync(string repo, string type, string name, CancellationToken ct = default)
    {
        CheckRef(type, name);
        var ctx = await RepoAsync(repo, ct);
        var full = FullRef(type, name);
        var commit = await ctx.Store.Refs.GetAsync(full, ct) ?? throw RhinoTrunkException.NotFound($"{type} '{name}' not found.");
        return ToRefInfo(full, commit);
    }

    public async Task<RefInfo> UpdateRefAsync(string repo, string type, string name, RefUpdateRequest request, string actor = "api", CancellationToken ct = default)
    {
        CheckRef(type, name);
        var ctx = await RepoAsync(repo, ct);
        if (!ContentHash.IsValid(request.Commit) || await ctx.GetCommitAsync(request.Commit, ct) is null)
            throw RhinoTrunkException.NotFound($"Commit {request.Commit} not found.");
        var full = FullRef(type, name);
        var current = await ctx.Store.Refs.GetAsync(full, ct);
        if (type == RefTypes.Tag && current is not null)
        {
            if (current == request.Commit) return ToRefInfo(full, current);
            throw RhinoTrunkException.Conflict(ErrorCodes.TagImmutable, $"Tag '{name}' already exists and cannot be moved.");
        }
        if (current != request.Expected)
            throw RefConflict(full, current);
        if (type == RefTypes.Branch && current is not null && request.Author?.Kind != AuthorKinds.Human && await IsProtectedAsync(ctx, name, ct))
            throw Forbidden(ErrorCodes.Forbidden, $"'{name}' is protected: only a human (author.kind = human) can move it directly. Agents merge with an approval.");
        if (request.Parent is not null && !Names.IsValidRefName(request.Parent))
            throw RhinoTrunkException.BadRequest($"Invalid parent branch '{request.Parent}'.");
        if (request.Author?.Id is { Length: > 0 } id) actor = id;
        var log = new RefLogEntry(full, current, request.Commit, actor, Now(), current is null ? "create" : "update");
        if (!await ctx.Store.Refs.CompareAndSwapAsync(full, current, request.Commit, log, ct))
            throw RefConflict(full, await ctx.Store.Refs.GetAsync(full, ct));
        if (type == RefTypes.Branch && current is null)
            await RecordBranchAsync(ctx, name, request.Parent, request.Commit, request.Author, ct);
        return ToRefInfo(full, request.Commit);
    }

    public async Task DeleteRefAsync(string repo, string type, string name, string? expected, string actor = "api", CancellationToken ct = default)
    {
        CheckRef(type, name);
        var ctx = await RepoAsync(repo, ct);
        var full = FullRef(type, name);
        var current = await ctx.Store.Refs.GetAsync(full, ct) ?? throw RhinoTrunkException.NotFound($"{type} '{name}' not found.");
        if (current != expected) throw RefConflict(full, current);
        if (type == RefTypes.Branch && await IsProtectedAsync(ctx, name, ct))
            throw Forbidden(ErrorCodes.Forbidden, $"'{name}' is protected and cannot be deleted. Change the policy first.");
        var log = new RefLogEntry(full, current, null, actor, Now(), "delete");
        if (!await ctx.Store.Refs.CompareAndSwapAsync(full, current, null, log, ct))
            throw RefConflict(full, await ctx.Store.Refs.GetAsync(full, ct));
        if (type == RefTypes.Branch) await WriteMetaAsync<BranchInfo>(ctx, BranchKeyPrefix + name, null, ct);
    }

    private static RhinoTrunkException RefConflict(string fullName, string? current) =>
        RhinoTrunkException.Conflict(ErrorCodes.RefConflict,
            $"{fullName} is at {current ?? "(none)"}, not at the expected commit. Fetch, rebase or merge, then retry.",
            new Dictionary<string, string> { ["ref"] = fullName, ["current"] = current ?? "" });

    // =========================================================================================
    // Reading snapshots
    // =========================================================================================

    /// <summary>Resolves a commit hash, branch name or tag name (in that order) to a commit hash.</summary>
    public async Task<string> ResolveAsync(string repo, string rev, CancellationToken ct = default) =>
        await ResolveAsync(await RepoAsync(repo, ct), rev, ct);

    private static async Task<string> ResolveAsync(RepoContext ctx, string rev, CancellationToken ct)
    {
        rev = Uri.UnescapeDataString(rev ?? "");
        if (ContentHash.IsValid(rev))
        {
            if (await ctx.GetCommitAsync(rev, ct) is null) throw RhinoTrunkException.NotFound($"Commit {rev} not found.");
            return rev;
        }
        var refs = await ctx.Store.Refs.ListAsync(ct);
        if (refs.TryGetValue(HeadsPrefix + rev, out var branch)) return branch;
        if (refs.TryGetValue(TagsPrefix + rev, out var tag)) return tag;
        throw RhinoTrunkException.NotFound($"No commit, branch or tag named '{rev}'.");
    }

    public async Task<CommitInfo> GetCommitAsync(string repo, string rev, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var id = await ResolveAsync(ctx, rev, ct);
        return (await ctx.RequireCommitAsync(id, ct)).ToInfo(id);
    }

    public async Task<EntitiesResponse> ListEntitiesAsync(string repo, string rev, string? doc, string? kind, bool includeManifests, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var id = await ResolveAsync(ctx, rev, ct);
        var commit = await ctx.RequireCommitAsync(id, ct);
        var entries = await ctx.Trees.ListAsync(commit.Tree, doc, kind, ct);
        var result = new List<EntityVersion>(entries.Count);
        foreach (var (key, version) in entries)
        {
            result.Add(new EntityVersion
            {
                Doc = key.Doc, Kind = key.Kind, Id = key.Id, Version = version,
                Manifest = includeManifests ? await ctx.Manifests.GetAsync(version, ct) : null,
            });
        }
        return new EntitiesResponse { Commit = id, Entities = result };
    }

    public async Task<EntityVersion> GetEntityAsync(string repo, string rev, string doc, string kind, string entityId, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var id = await ResolveAsync(ctx, rev, ct);
        var commit = await ctx.RequireCommitAsync(id, ct);
        var key = new EntityKey(doc, kind, entityId);
        var version = await ctx.Trees.GetAsync(commit.Tree, key, ct)
                      ?? throw RhinoTrunkException.NotFound($"{kind} {entityId} does not exist in document '{doc}' at {id}.");
        return new EntityVersion { Doc = doc, Kind = kind, Id = entityId, Version = version, Manifest = await ctx.Manifests.GetAsync(version, ct) };
    }

    /// <summary>Entity- and component-level differences. <paramref name="fromRev"/> null means the empty snapshot.</summary>
    public async Task<DiffResponse> DiffAsync(string repo, string? fromRev, string toRev, bool includeManifests = false, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        string? fromId = string.IsNullOrEmpty(fromRev) ? null : await ResolveAsync(ctx, fromRev, ct);
        var toId = await ResolveAsync(ctx, toRev, ct);
        var fromTree = fromId is null ? await ctx.Trees.EmptyRootAsync(ct) : (await ctx.RequireCommitAsync(fromId, ct)).Tree;
        var toTree = (await ctx.RequireCommitAsync(toId, ct)).Tree;
        var changes = new List<DiffEntry>();
        foreach (var d in await ctx.Trees.DiffAsync(fromTree, toTree, ct))
            changes.Add(await DescribeAsync(ctx, d, includeManifests, ct));
        return new DiffResponse { From = fromId, To = toId, Changes = changes };
    }

    /// <summary>The best common ancestor of two revisions (null for unrelated histories).</summary>
    public async Task<MergeBaseResponse> MergeBaseAsync(string repo, string a, string b, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var ia = await ResolveAsync(ctx, a, ct);
        var ib = await ResolveAsync(ctx, b, ct);
        return new MergeBaseResponse { A = ia, B = ib, MergeBase = await MergeBaseAsync(ctx, ia, ib, ct) };
    }

    private static async Task<DiffEntry> DescribeAsync(RepoContext ctx, TreeDiffEntry d, bool includeManifests, CancellationToken ct)
    {
        var entry = new DiffEntry
        {
            Doc = d.Key.Doc, Kind = d.Key.Kind, Id = d.Key.Id, From = d.From, To = d.To,
            Change = d.From is null ? DiffChanges.Added : d.To is null ? DiffChanges.Deleted : DiffChanges.Modified,
        };
        if (includeManifests)
            entry = entry with
            {
                FromManifest = d.From is null ? null : await ctx.Manifests.GetAsync(d.From, ct),
                ToManifest = d.To is null ? null : await ctx.Manifests.GetAsync(d.To, ct),
            };
        if (d.From is null || d.To is null) return entry;
        var a = await ctx.Manifests.GetAsync(d.From, ct);
        var b = await ctx.Manifests.GetAsync(d.To, ct);
        if (a is null || b is null) return entry;
        var components = a.Components.Keys.Union(b.Components.Keys, StringComparer.Ordinal)
            .Where(n => a.Components.GetValueOrDefault(n) != b.Components.GetValueOrDefault(n))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        return entry with
        {
            Components = components,
            RefsChanged = !a.Refs.SequenceEqual(b.Refs),
            SubtypeChanged = a.Subtype != b.Subtype,
        };
    }

    public async Task<HistoryResponse> HistoryAsync(string repo, string rev, string doc, string kind, string entityId, int limit, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var key = new EntityKey(doc, kind, entityId);
        string? current = await ResolveAsync(ctx, rev, ct);
        var entries = new List<HistoryEntry>();
        while (current is not null && entries.Count < Math.Clamp(limit, 1, 1000))
        {
            var commit = await ctx.RequireCommitAsync(current, ct);
            var version = await ctx.Trees.GetAsync(commit.Tree, key, ct);
            var parentId = commit.Parents.FirstOrDefault();
            var parentVersion = parentId is null ? null : await ctx.Trees.GetAsync((await ctx.RequireCommitAsync(parentId, ct)).Tree, key, ct);
            if (version != parentVersion)
            {
                entries.Add(new HistoryEntry
                {
                    Commit = current, Version = version, Timestamp = commit.Timestamp, Message = commit.Message, Author = commit.Author,
                    Change = parentVersion is null ? DiffChanges.Added : version is null ? DiffChanges.Deleted : DiffChanges.Modified,
                });
            }
            current = parentId;
        }
        return new HistoryResponse { Doc = doc, Kind = kind, Id = entityId, Entries = entries };
    }

    // =========================================================================================
    // Graph
    // =========================================================================================

    private static readonly IComparer<(int Gen, long Ticks, string Id)> Descending =
        Comparer<(int Gen, long Ticks, string Id)>.Create((a, b) =>
        {
            var c = b.Gen.CompareTo(a.Gen);
            if (c != 0) return c;
            c = b.Ticks.CompareTo(a.Ticks);
            return c != 0 ? c : string.CompareOrdinal(b.Id, a.Id);
        });

    /// <summary>Commits reachable from the given revisions (default: all refs), newest first, children before parents.</summary>
    public async Task<GraphResponse> GraphAsync(string repo, IReadOnlyList<string>? revs, int limit, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        limit = Math.Clamp(limit, 1, 10_000);
        var refs = await ctx.Store.Refs.ListAsync(ct);
        var labels = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (full, commit) in refs.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var info = ToRefInfo(full, commit);
            if (!labels.TryGetValue(commit, out var list)) labels[commit] = list = [];
            list.Add(info.Type == RefTypes.Tag ? "tag:" + info.Name : info.Name);
        }

        var heads = new List<string>();
        if (revs is { Count: > 0 })
            foreach (var rev in revs) heads.Add(await ResolveAsync(ctx, rev, ct));
        else
            heads.AddRange(refs.Values);

        var queue = new PriorityQueue<string, (int, long, string)>(Descending);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var head in heads.Distinct(StringComparer.Ordinal))
        {
            var c = await ctx.RequireCommitAsync(head, ct);
            seen.Add(head);
            queue.Enqueue(head, (c.Generation, c.Timestamp.UtcTicks, head));
        }

        var result = new List<GraphCommit>();
        while (queue.Count > 0 && result.Count < limit)
        {
            var id = queue.Dequeue();
            var commit = await ctx.RequireCommitAsync(id, ct);
            result.Add(new GraphCommit
            {
                Id = id, Parents = commit.Parents, Generation = commit.Generation, Timestamp = commit.Timestamp,
                Message = commit.Message, Author = commit.Author, Refs = labels.GetValueOrDefault(id) ?? [],
            });
            foreach (var parent in commit.Parents)
            {
                if (!seen.Add(parent)) continue;
                var p = await ctx.RequireCommitAsync(parent, ct);
                queue.Enqueue(parent, (p.Generation, p.Timestamp.UtcTicks, parent));
            }
        }
        return new GraphResponse { Commits = result, Truncated = queue.Count > 0 };
    }

    /// <summary>True if <paramref name="ancestor"/> equals or is reachable from <paramref name="descendant"/>.</summary>
    internal static async Task<bool> IsAncestorAsync(RepoContext ctx, string ancestor, string descendant, CancellationToken ct)
    {
        var target = await ctx.RequireCommitAsync(ancestor, ct);
        var stack = new Stack<string>([descendant]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { descendant };
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id == ancestor) return true;
            var commit = await ctx.RequireCommitAsync(id, ct);
            if (commit.Generation <= target.Generation) continue;
            foreach (var p in commit.Parents)
                if (seen.Add(p)) stack.Push(p);
        }
        return false;
    }

    /// <summary>The best common ancestor (highest generation), or null for unrelated histories.</summary>
    internal static async Task<string?> MergeBaseAsync(RepoContext ctx, string a, string b, CancellationToken ct)
    {
        var ancestorsOfA = new HashSet<string>(StringComparer.Ordinal) { a };
        var stack = new Stack<string>([a]);
        while (stack.Count > 0)
            foreach (var p in (await ctx.RequireCommitAsync(stack.Pop(), ct)).Parents)
                if (ancestorsOfA.Add(p)) stack.Push(p);

        var queue = new PriorityQueue<string, (int, long, string)>(Descending);
        var seen = new HashSet<string>(StringComparer.Ordinal) { b };
        var cb = await ctx.RequireCommitAsync(b, ct);
        queue.Enqueue(b, (cb.Generation, cb.Timestamp.UtcTicks, b));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (ancestorsOfA.Contains(id)) return id;
            foreach (var p in (await ctx.RequireCommitAsync(id, ct)).Parents)
            {
                if (!seen.Add(p)) continue;
                var pc = await ctx.RequireCommitAsync(p, ct);
                queue.Enqueue(p, (pc.Generation, pc.Timestamp.UtcTicks, p));
            }
        }
        return null;
    }

    // =========================================================================================
    // Annotations and derived data
    // =========================================================================================

    public async Task<Annotation> AddAnnotationAsync(string repo, string rev, Annotation annotation, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var id = await ResolveAsync(ctx, rev, ct);
        if (!Names.IsValidNamespace(annotation.Namespace))
            throw RhinoTrunkException.BadRequest("Invalid annotation namespace (lowercase letters, digits, '.', '_', '/', '-').");
        if (annotation.Status is not (AnnotationStatuses.Pending or AnnotationStatuses.Success or AnnotationStatuses.Failure or AnnotationStatuses.Info))
            throw RhinoTrunkException.BadRequest("Annotation status must be pending, success, failure or info.");
        var stored = annotation with { Timestamp = Now() };
        await ctx.Store.Annotations.AppendAsync(id, stored, ct);
        return stored;
    }

    public async Task<AnnotationsResponse> ListAnnotationsAsync(string repo, string rev, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var id = await ResolveAsync(ctx, rev, ct);
        return new AnnotationsResponse { Commit = id, Annotations = (await ctx.Store.Annotations.ListAsync(id, ct)).ToList() };
    }

    public async Task<DerivedRecord> SetDerivedAsync(string repo, string source, string recipe, DerivedPutRequest request, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        if (!ContentHash.IsValid(source) || !ContentHash.IsValid(request.Result))
            throw RhinoTrunkException.BadRequest("Source and result must be content hashes.");
        if (!Names.IsValidRecipe(recipe))
            throw RhinoTrunkException.BadRequest("Invalid recipe name (lowercase letters, digits, '.', '_', '-').");
        if (!await ctx.Store.Objects.ExistsAsync(source, ct))
            throw RhinoTrunkException.Unprocessable(ErrorCodes.ObjectMissing, $"Source object {source} does not exist.");
        if (!await ctx.Store.Objects.ExistsAsync(request.Result, ct))
            throw RhinoTrunkException.Unprocessable(ErrorCodes.ObjectMissing, $"Result object {request.Result} has not been uploaded.");
        var record = new DerivedRecord { Source = source, Recipe = recipe, Result = request.Result };
        await ctx.Store.Derived.SetAsync(record, ct);
        return record;
    }

    public async Task<DerivedRecord> GetDerivedAsync(string repo, string source, string recipe, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        if (!ContentHash.IsValid(source)) throw RhinoTrunkException.BadRequest("Source must be a content hash.");
        return await ctx.Store.Derived.GetAsync(source, recipe, ct)
               ?? throw RhinoTrunkException.NotFound($"No derived '{recipe}' for {source}.");
    }
}

/// <summary>Per-repository caches and the write lock.</summary>
internal sealed class RepoContext
{
    private readonly ConcurrentDictionary<string, CommitObject> _commits = new(StringComparer.Ordinal);

    public RepoContext(IRepositoryStore store)
    {
        Store = store;
        Trees = new TreeStore(store.Objects);
        Manifests = new ManifestCache(store.Objects);
        Validator = new SnapshotValidator(store.Objects, Trees, Manifests);
    }

    public IRepositoryStore Store { get; }
    public TreeStore Trees { get; }
    public ManifestCache Manifests { get; }
    public SnapshotValidator Validator { get; }
    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    public async Task<CommitObject?> GetCommitAsync(string hash, CancellationToken ct)
    {
        if (_commits.TryGetValue(hash, out var cached)) return cached;
        var commit = await Store.Objects.GetTypedJsonAsync<CommitObject>(hash, "commit", ct);
        if (commit is null || !ContentHash.IsValid(commit.Tree)) return null;
        _commits[hash] = commit;
        return commit;
    }

    public async Task<CommitObject> RequireCommitAsync(string hash, CancellationToken ct) =>
        await GetCommitAsync(hash, ct) ?? throw RhinoTrunkException.NotFound($"Commit {hash} not found.");
}
