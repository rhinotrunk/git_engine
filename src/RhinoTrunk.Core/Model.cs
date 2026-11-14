using System.Text.Json;
using RhinoTrunk.Contracts;
using RhinoTrunk.Core.Storage;

namespace RhinoTrunk.Core;

/// <summary>Address of an entity inside a snapshot.</summary>
public readonly record struct EntityKey(string Doc, string Kind, string Id) : IComparable<EntityKey>
{
    public int CompareTo(EntityKey other)
    {
        var c = string.CompareOrdinal(Doc, other.Doc);
        if (c != 0) return c;
        c = string.CompareOrdinal(Kind, other.Kind);
        return c != 0 ? c : string.CompareOrdinal(Id, other.Id);
    }

    public override string ToString() => $"{Doc}/{Kind}/{Id}";
}

// Stored node formats. Every node is canonical JSON; its hash is its identity.
// root -> docs[docId] -> kinds[kind] -> shards[2 hex chars of sha256(entityId)] -> entries[entityId] = manifest hash

public sealed record RootNode
{
    public string Type { get; init; } = "root";
    public int Format { get; init; } = 1;
    public Dictionary<string, string> Docs { get; init; } = new(StringComparer.Ordinal);
}

public sealed record DocNode
{
    public string Type { get; init; } = "doc";
    public Dictionary<string, string> Kinds { get; init; } = new(StringComparer.Ordinal);
}

public sealed record KindNode
{
    public string Type { get; init; } = "kind";
    public Dictionary<string, string> Shards { get; init; } = new(StringComparer.Ordinal);
}

public sealed record LeafNode
{
    public string Type { get; init; } = "leaf";
    public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>A commit as stored. Its canonical JSON hash is the commit id.</summary>
public sealed record CommitObject
{
    public string Type { get; init; } = "commit";
    public int Format { get; init; } = 1;
    public string Tree { get; init; } = "";
    public List<string> Parents { get; init; } = [];
    /// <summary>1 + max(parent generations); a pure function of the parents, used for graph layout and merge-base search.</summary>
    public int Generation { get; init; }
    public AuthorInfo Author { get; init; } = new();
    public DateTimeOffset Timestamp { get; init; }
    public string Message { get; init; } = "";
    public Dictionary<string, string>? Meta { get; init; }

    public CommitInfo ToInfo(string id) => new()
    {
        Id = id, Tree = Tree, Parents = Parents, Generation = Generation, Author = Author,
        Timestamp = Timestamp, Message = Message, Meta = Meta,
    };
}

public static class ObjectStoreJson
{
    /// <summary>Stores a value as canonical JSON and returns its content hash.</summary>
    public static async Task<string> PutJsonAsync<T>(this IObjectStore store, T value, CancellationToken ct = default)
    {
        var bytes = CanonicalJson.Serialize(value);
        var hash = ContentHash.Compute(bytes);
        await store.PutAsync(hash, bytes, ct);
        return hash;
    }

    public static async Task<T?> GetJsonAsync<T>(this IObjectStore store, string hash, CancellationToken ct = default) where T : class
    {
        var bytes = await store.GetAsync(hash, ct);
        if (bytes is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, RhinoTrunkJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads an object only if it is JSON whose "type" member equals <paramref name="type"/>
    /// ("manifest", "commit", "root", ...). Returns null for missing objects and for anything else.
    /// </summary>
    public static async Task<T?> GetTypedJsonAsync<T>(this IObjectStore store, string hash, string type, CancellationToken ct = default) where T : class
    {
        var bytes = await store.GetAsync(hash, ct);
        if (bytes is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("type", out var t)
                || t.ValueKind != JsonValueKind.String
                || t.GetString() != type)
                return null;
            return doc.RootElement.Deserialize<T>(RhinoTrunkJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Error with an HTTP status and machine-readable code; mapped 1:1 to <see cref="ApiError"/>.</summary>
public sealed class RhinoTrunkException(int status, string code, string message, List<ApiErrorDetail>? errors = null, Dictionary<string, string>? data = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public List<ApiErrorDetail>? Errors { get; } = errors;
    public Dictionary<string, string>? Details { get; } = data;

    public ApiError ToApiError() => new() { Code = Code, Message = Message, Errors = Errors, Data = Details };

    public static RhinoTrunkException BadRequest(string message, List<ApiErrorDetail>? errors = null) =>
        new(400, ErrorCodes.InvalidRequest, message, errors);

    public static RhinoTrunkException NotFound(string message) => new(404, ErrorCodes.NotFound, message);

    public static RhinoTrunkException Conflict(string code, string message, Dictionary<string, string>? data = null, List<ApiErrorDetail>? errors = null) =>
        new(409, code, message, errors, data);

    public static RhinoTrunkException Unprocessable(string code, string message, List<ApiErrorDetail>? errors = null) =>
        new(422, code, message, errors);
}
