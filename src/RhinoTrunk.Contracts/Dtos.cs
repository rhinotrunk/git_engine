using System.Text.Json.Nodes;

namespace RhinoTrunk.Contracts;

// ---------------------------------------------------------------------------------------------
// Entities and manifests
// ---------------------------------------------------------------------------------------------

/// <summary>Who made a commit. <see cref="Kind"/> is "human" or "agent".</summary>
public sealed record AuthorInfo
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public string Kind { get; init; } = AuthorKinds.Human;
}

/// <summary>A reference from one entity to another entity in the same document.</summary>
public sealed record EntityRef
{
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    public string Role { get; init; } = "";
}

/// <summary>
/// One immutable version of one entity, as stored. Its content hash is the entity's "version".
/// It deliberately contains no entity id, so identical copies share storage.
/// </summary>
public sealed record Manifest
{
    public string Type { get; init; } = "manifest";
    public int Format { get; init; } = 1;
    public string Kind { get; init; } = "";
    public string? Subtype { get; init; }
    /// <summary>Component name (e.g. "geometry", "attributes", "userdata", "payload") to blob hash.</summary>
    public Dictionary<string, string> Components { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Sorted by kind, id, role.</summary>
    public List<EntityRef> Refs { get; init; } = [];
}

/// <summary>
/// Manifest as sent by a client in a commit. <see cref="Attributes"/> may be given inline;
/// the server stores it as a canonical JSON blob under the "attributes" component.
/// </summary>
public sealed record ManifestInput
{
    public string? Kind { get; init; }
    public string? Subtype { get; init; }
    public JsonObject? Attributes { get; init; }
    public Dictionary<string, string>? Components { get; init; }
    public List<EntityRef>? Refs { get; init; }
}

/// <summary>One change in a commit: put a new version of an entity, or delete it.</summary>
public sealed record Change
{
    public string Op { get; init; } = ChangeOps.Put;
    public string Doc { get; init; } = Names.DefaultDoc;
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    /// <summary>For "put": the new version, inline. Mutually exclusive with <see cref="Version"/>.</summary>
    public ManifestInput? Manifest { get; init; }
    /// <summary>For "put": hash of an existing manifest (e.g. to restore an old version).</summary>
    public string? Version { get; init; }
}

/// <summary>An entity as listed in a snapshot.</summary>
public sealed record EntityVersion
{
    public string Doc { get; init; } = Names.DefaultDoc;
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public Manifest? Manifest { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Repositories and objects
// ---------------------------------------------------------------------------------------------

public sealed record CreateRepoRequest
{
    public string Name { get; init; } = "";
}

public sealed record RepoInfo
{
    public string Name { get; init; } = "";
    public DateTimeOffset Created { get; init; }
}

public sealed record ReposResponse
{
    public List<RepoInfo> Repos { get; init; } = [];
}

public sealed record HashesRequest
{
    public List<string> Hashes { get; init; } = [];
}

public sealed record MissingResponse
{
    public List<string> Missing { get; init; } = [];
}

public sealed record BatchGetResponse
{
    /// <summary>Hash to base64-encoded bytes.</summary>
    public Dictionary<string, string> Objects { get; init; } = [];
    public List<string> Missing { get; init; } = [];
}

public sealed record PutObjectResponse
{
    public string Hash { get; init; } = "";
    public bool Created { get; init; }
    public long Size { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Commits
// ---------------------------------------------------------------------------------------------

public sealed record CommitRequest
{
    /// <summary>Branch to advance (created if it does not exist and <see cref="ExpectedHead"/> is null).</summary>
    public string Branch { get; init; } = Names.DefaultBranch;
    /// <summary>The branch head the client based its changes on; null for the first commit of a new branch.</summary>
    public string? ExpectedHead { get; init; }
    public string Message { get; init; } = "";
    public AuthorInfo Author { get; init; } = new();
    public Dictionary<string, string>? Meta { get; init; }
    public List<Change> Changes { get; init; } = [];
}

public sealed record CommitResponse
{
    public string Commit { get; init; } = "";
    public string Tree { get; init; } = "";
    public string Branch { get; init; } = "";
    /// <summary>The resolved version hash of every "put" change, in request order.</summary>
    public List<EntityVersion> Versions { get; init; } = [];
}

public sealed record CommitInfo
{
    public string Id { get; init; } = "";
    public string Tree { get; init; } = "";
    public List<string> Parents { get; init; } = [];
    public int Generation { get; init; }
    public AuthorInfo Author { get; init; } = new();
    public DateTimeOffset Timestamp { get; init; }
    public string Message { get; init; } = "";
    public Dictionary<string, string>? Meta { get; init; }
}

public sealed record EntitiesResponse
{
    public string Commit { get; init; } = "";
    public List<EntityVersion> Entities { get; init; } = [];
}

// ---------------------------------------------------------------------------------------------
// Diff, graph, history
// ---------------------------------------------------------------------------------------------

public sealed record DiffEntry
{
    public string Doc { get; init; } = Names.DefaultDoc;
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    /// <summary>"added", "deleted" or "modified".</summary>
    public string Change { get; init; } = "";
    public string? From { get; init; }
    public string? To { get; init; }
    /// <summary>For "modified": names of components whose hash changed (added/removed components included).</summary>
    public List<string>? Components { get; init; }
    public bool? RefsChanged { get; init; }
    public bool? SubtypeChanged { get; init; }
}

public sealed record DiffResponse
{
    public string? From { get; init; }
    public string To { get; init; } = "";
    public List<DiffEntry> Changes { get; init; } = [];
}

public sealed record GraphCommit
{
    public string Id { get; init; } = "";
    public List<string> Parents { get; init; } = [];
    public int Generation { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Message { get; init; } = "";
    public AuthorInfo Author { get; init; } = new();
    /// <summary>Names of refs pointing at this commit, e.g. "main", "tag:v1".</summary>
    public List<string> Refs { get; init; } = [];
}

public sealed record GraphResponse
{
    /// <summary>Newest first; every commit appears before its parents.</summary>
    public List<GraphCommit> Commits { get; init; } = [];
    public bool Truncated { get; init; }
}

public sealed record HistoryEntry
{
    public string Commit { get; init; } = "";
    public string Change { get; init; } = "";
    public string? Version { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Message { get; init; } = "";
    public AuthorInfo Author { get; init; } = new();
}

public sealed record HistoryResponse
{
    public string Doc { get; init; } = Names.DefaultDoc;
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    public List<HistoryEntry> Entries { get; init; } = [];
}

// ---------------------------------------------------------------------------------------------
// Refs and merges
// ---------------------------------------------------------------------------------------------

public sealed record RefInfo
{
    public string Name { get; init; } = "";
    /// <summary>"branch" or "tag".</summary>
    public string Type { get; init; } = RefTypes.Branch;
    public string Commit { get; init; } = "";
}

public sealed record RefsResponse
{
    public List<RefInfo> Refs { get; init; } = [];
}

public sealed record RefUpdateRequest
{
    public string Commit { get; init; } = "";
    /// <summary>Current value the client expects; null means the ref must not exist yet.</summary>
    public string? Expected { get; init; }
}

public sealed record MergeResolution
{
    public string Doc { get; init; } = Names.DefaultDoc;
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    /// <summary>"ours" or "theirs".</summary>
    public string Take { get; init; } = MergeTakes.Ours;
}

public sealed record MergeRequest
{
    /// <summary>Branch that receives the merge ("ours").</summary>
    public string Branch { get; init; } = Names.DefaultBranch;
    public string? ExpectedHead { get; init; }
    /// <summary>Branch name, tag name or commit hash to merge in.</summary>
    public string Theirs { get; init; } = "";
    public string? Message { get; init; }
    public AuthorInfo Author { get; init; } = new();
    public bool NoFastForward { get; init; }
    public List<MergeResolution>? Resolutions { get; init; }
}

public sealed record MergeResponse
{
    /// <summary>"merged", "fastForward" or "upToDate".</summary>
    public string Status { get; init; } = "";
    public string Commit { get; init; } = "";
    public string? MergeBase { get; init; }
    public string Branch { get; init; } = "";
}

// ---------------------------------------------------------------------------------------------
// Annotations and derived data
// ---------------------------------------------------------------------------------------------

/// <summary>Mutable information about a commit (CI checks, reviews, agent logs). Never part of a snapshot.</summary>
public sealed record Annotation
{
    public string Namespace { get; init; } = "";
    public string Status { get; init; } = AnnotationStatuses.Info;
    public string? Summary { get; init; }
    public JsonObject? Data { get; init; }
    public AuthorInfo? Author { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
}

public sealed record AnnotationsResponse
{
    public string Commit { get; init; } = "";
    public List<Annotation> Annotations { get; init; } = [];
}

/// <summary>Derived data keyed by content: (source hash, recipe) to result hash, e.g. preview meshes.</summary>
public sealed record DerivedRecord
{
    public string Source { get; init; } = "";
    public string Recipe { get; init; } = "";
    public string Result { get; init; } = "";
}

public sealed record DerivedPutRequest
{
    public string Result { get; init; } = "";
}

// ---------------------------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------------------------

public sealed record ApiErrorDetail
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Doc { get; init; }
    public string? Kind { get; init; }
    public string? Id { get; init; }
    public List<string>? Fields { get; init; }
}

public sealed record ApiError
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public List<ApiErrorDetail>? Errors { get; init; }
    public Dictionary<string, string>? Data { get; init; }
}
