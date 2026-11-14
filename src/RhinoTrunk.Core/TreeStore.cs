using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RhinoTrunk.Core.Storage;

namespace RhinoTrunk.Core;

public sealed record TreeChange(EntityKey Key, string? Version); // Version null = delete

public sealed record TreeDiffEntry(EntityKey Key, string? From, string? To);

/// <summary>
/// Merkle tree of a snapshot: root -> document -> kind -> 256 shards -> entries.
/// Unchanged subtrees keep their hash, so commits rewrite only touched paths and
/// diffs skip every subtree whose hash is equal on both sides.
/// The layout is history independent: the same set of entries always yields the same root hash.
/// </summary>
public sealed class TreeStore(IObjectStore objects)
{
    private readonly ConcurrentDictionary<string, object> _cache = new(StringComparer.Ordinal);
    private string? _emptyRoot;

    public static string ShardOf(string entityId)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(entityId), digest);
        return Convert.ToHexString(digest[..1]).ToLowerInvariant();
    }

    public async Task<string> EmptyRootAsync(CancellationToken ct = default) =>
        _emptyRoot ??= await objects.PutJsonAsync(new RootNode(), ct);

    private async Task<T> ReadAsync<T>(string hash, CancellationToken ct) where T : class
    {
        if (_cache.TryGetValue(hash, out var cached)) return (T)cached;
        var node = await objects.GetJsonAsync<T>(hash, ct)
                   ?? throw new InvalidDataException($"Tree node {hash} is missing or unreadable.");
        _cache[hash] = node;
        return node;
    }

    private async Task<string> WriteAsync<T>(T node, CancellationToken ct) where T : class
    {
        var hash = await objects.PutJsonAsync(node, ct);
        _cache[hash] = node;
        return hash;
    }

    public async Task<string> ApplyAsync(string rootHash, IEnumerable<TreeChange> changes, CancellationToken ct = default)
    {
        var root = await ReadAsync<RootNode>(rootHash, ct);
        var docs = new Dictionary<string, string>(root.Docs, StringComparer.Ordinal);
        foreach (var docGroup in changes.GroupBy(c => c.Key.Doc, StringComparer.Ordinal))
        {
            var doc = docs.TryGetValue(docGroup.Key, out var dh) ? await ReadAsync<DocNode>(dh, ct) : new DocNode();
            var kinds = new Dictionary<string, string>(doc.Kinds, StringComparer.Ordinal);
            foreach (var kindGroup in docGroup.GroupBy(c => c.Key.Kind, StringComparer.Ordinal))
            {
                var kind = kinds.TryGetValue(kindGroup.Key, out var kh) ? await ReadAsync<KindNode>(kh, ct) : new KindNode();
                var shards = new Dictionary<string, string>(kind.Shards, StringComparer.Ordinal);
                foreach (var shardGroup in kindGroup.GroupBy(c => ShardOf(c.Key.Id), StringComparer.Ordinal))
                {
                    var leaf = shards.TryGetValue(shardGroup.Key, out var lh) ? await ReadAsync<LeafNode>(lh, ct) : new LeafNode();
                    var entries = new Dictionary<string, string>(leaf.Entries, StringComparer.Ordinal);
                    foreach (var change in shardGroup)
                    {
                        if (change.Version is null) entries.Remove(change.Key.Id);
                        else entries[change.Key.Id] = change.Version;
                    }
                    if (entries.Count == 0) shards.Remove(shardGroup.Key);
                    else shards[shardGroup.Key] = await WriteAsync(new LeafNode { Entries = entries }, ct);
                }
                if (shards.Count == 0) kinds.Remove(kindGroup.Key);
                else kinds[kindGroup.Key] = await WriteAsync(new KindNode { Shards = shards }, ct);
            }
            if (kinds.Count == 0) docs.Remove(docGroup.Key);
            else docs[docGroup.Key] = await WriteAsync(new DocNode { Kinds = kinds }, ct);
        }
        return await WriteAsync(new RootNode { Docs = docs }, ct);
    }

    public async Task<string?> GetAsync(string rootHash, EntityKey key, CancellationToken ct = default)
    {
        var root = await ReadAsync<RootNode>(rootHash, ct);
        if (!root.Docs.TryGetValue(key.Doc, out var dh)) return null;
        var doc = await ReadAsync<DocNode>(dh, ct);
        if (!doc.Kinds.TryGetValue(key.Kind, out var kh)) return null;
        var kind = await ReadAsync<KindNode>(kh, ct);
        if (!kind.Shards.TryGetValue(ShardOf(key.Id), out var lh)) return null;
        var leaf = await ReadAsync<LeafNode>(lh, ct);
        return leaf.Entries.TryGetValue(key.Id, out var version) ? version : null;
    }

    public async Task<IReadOnlyList<string>> ListDocsAsync(string rootHash, CancellationToken ct = default) =>
        (await ReadAsync<RootNode>(rootHash, ct)).Docs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>All entries, optionally filtered by document and kind, sorted by key.</summary>
    public async Task<List<(EntityKey Key, string Version)>> ListAsync(string rootHash, string? docFilter = null, string? kindFilter = null, CancellationToken ct = default)
    {
        var result = new List<(EntityKey, string)>();
        var root = await ReadAsync<RootNode>(rootHash, ct);
        foreach (var (docName, dh) in root.Docs)
        {
            if (docFilter is not null && docName != docFilter) continue;
            var doc = await ReadAsync<DocNode>(dh, ct);
            foreach (var (kindName, kh) in doc.Kinds)
            {
                if (kindFilter is not null && kindName != kindFilter) continue;
                var kind = await ReadAsync<KindNode>(kh, ct);
                foreach (var lh in kind.Shards.Values)
                {
                    var leaf = await ReadAsync<LeafNode>(lh, ct);
                    foreach (var (id, version) in leaf.Entries)
                        result.Add((new EntityKey(docName, kindName, id), version));
                }
            }
        }
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    public async Task<int> CountAsync(string rootHash, string doc, string kind, CancellationToken ct = default)
    {
        var root = await ReadAsync<RootNode>(rootHash, ct);
        if (!root.Docs.TryGetValue(doc, out var dh)) return 0;
        var docNode = await ReadAsync<DocNode>(dh, ct);
        if (!docNode.Kinds.TryGetValue(kind, out var kh)) return 0;
        var kindNode = await ReadAsync<KindNode>(kh, ct);
        var count = 0;
        foreach (var lh in kindNode.Shards.Values) count += (await ReadAsync<LeafNode>(lh, ct)).Entries.Count;
        return count;
    }

    /// <summary>Entity-level differences between two snapshots. Cost follows the size of the change.</summary>
    public async Task<List<TreeDiffEntry>> DiffAsync(string fromRoot, string toRoot, CancellationToken ct = default)
    {
        var result = new List<TreeDiffEntry>();
        if (fromRoot == toRoot) return result;
        var a = await ReadAsync<RootNode>(fromRoot, ct);
        var b = await ReadAsync<RootNode>(toRoot, ct);
        foreach (var docName in Union(a.Docs, b.Docs))
        {
            a.Docs.TryGetValue(docName, out var ha);
            b.Docs.TryGetValue(docName, out var hb);
            if (ha == hb) continue;
            var da = ha is null ? new DocNode() : await ReadAsync<DocNode>(ha, ct);
            var db = hb is null ? new DocNode() : await ReadAsync<DocNode>(hb, ct);
            foreach (var kindName in Union(da.Kinds, db.Kinds))
            {
                da.Kinds.TryGetValue(kindName, out var ka);
                db.Kinds.TryGetValue(kindName, out var kb);
                if (ka == kb) continue;
                var na = ka is null ? new KindNode() : await ReadAsync<KindNode>(ka, ct);
                var nb = kb is null ? new KindNode() : await ReadAsync<KindNode>(kb, ct);
                foreach (var shard in Union(na.Shards, nb.Shards))
                {
                    na.Shards.TryGetValue(shard, out var la);
                    nb.Shards.TryGetValue(shard, out var lb);
                    if (la == lb) continue;
                    var ea = la is null ? new LeafNode() : await ReadAsync<LeafNode>(la, ct);
                    var eb = lb is null ? new LeafNode() : await ReadAsync<LeafNode>(lb, ct);
                    foreach (var id in Union(ea.Entries, eb.Entries))
                    {
                        ea.Entries.TryGetValue(id, out var va);
                        eb.Entries.TryGetValue(id, out var vb);
                        if (va != vb) result.Add(new TreeDiffEntry(new EntityKey(docName, kindName, id), va, vb));
                    }
                }
            }
        }
        result.Sort((x, y) => x.Key.CompareTo(y.Key));
        return result;
    }

    private static IEnumerable<string> Union(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Keys.Union(b.Keys, StringComparer.Ordinal);
}
