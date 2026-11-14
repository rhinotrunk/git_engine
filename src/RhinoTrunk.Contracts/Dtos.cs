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
    /// <summary>Only when this commit creates the branch: the branch it was split from (recorded as branch metadata).</summary>
    public string? Parent { get; init; }
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
    /// <summary>Manifests of both versions, only when requested (<c>manifests=true</c>), to save a round trip.</summary>
    public Manifest? FromManifest { get; init; }
    public Manifest? ToManifest { get; init; }
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
    /// <summary>Who moves the ref. Required (and human) to move a protected branch; recorded as the creator of a new branch.</summary>
    public AuthorInfo? Author { get; init; }
    /// <summary>When creating a branch: the branch it was split from.</summary>
    public string? Parent { get; init; }
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
    /// <summary>Extra rules, applied after the repository's rules (see <see cref="RepoPolicy.MergeRules"/>).</summary>
    public List<MergeRule>? Rules { get; init; }
    /// <summary>Apply the repository's merge rules (default true).</summary>
    public bool? UseRepoRules { get; init; }
    /// <summary>Id of a human approval (POST /approvals). Required when an agent merges into a protected branch,
    /// into a branch it did not create, or when rules resolved conflicts.</summary>
    public string? Approval { get; init; }
    /// <summary>A human author confirms that conflicts resolved by rules may be merged.</summary>
    public bool ApproveRuleResolutions { get; init; }
}

public sealed record MergeResponse
{
    /// <summary>"merged", "fastForward" or "upToDate".</summary>
    public string Status { get; init; } = "";
    public string Commit { get; init; } = "";
    public string? MergeBase { get; init; }
    public string Branch { get; init; } = "";
    /// <summary>Conflicts that merge rules resolved.</summary>
    public List<AutoResolution>? AutoResolved { get; init; }
    /// <summary>The human who approved the merge, when approval was required.</summary>
    public AuthorInfo? ApprovedBy { get; init; }
}

/// <summary>Resolves conflicts automatically. The first matching rule wins; explicit resolutions win over rules.</summary>
public sealed record MergeRule
{
    public string Name { get; init; } = "";
    /// <summary>Entity kind to match; null matches every kind.</summary>
    public string? Kind { get; init; }
    /// <summary>
    /// Conflicting fields this rule may resolve ("subtype", "refs", "components.geometry", "components.attributes", ...).
    /// A conflict matches only if all its fields are listed. Null matches every conflict of the kind,
    /// including add/add and modify/delete conflicts, which are then resolved for the whole entity.
    /// </summary>
    public List<string>? Fields { get; init; }
    /// <summary>"ours" or "theirs": the side whose value wins for the conflicting fields.</summary>
    public string Take { get; init; } = MergeTakes.Theirs;
}

public sealed record AutoResolution
{
    public string Doc { get; init; } = Names.DefaultDoc;
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    public List<string>? Fields { get; init; }
    public string Take { get; init; } = "";
    public string Rule { get; init; } = "";
}

public sealed record MergeConflict
{
    public string Doc { get; init; } = Names.DefaultDoc;
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    public string Reason { get; init; } = "";
    /// <summary>Conflicting manifest fields when both sides modified the entity.</summary>
    public List<string>? Fields { get; init; }
    /// <summary>Versions at the merge base and on each side (null = absent), so a client can draw both sides.</summary>
    public string? Base { get; init; }
    public string? Ours { get; init; }
    public string? Theirs { get; init; }
}

/// <summary>Dry run of a merge. No ref moves; the merged snapshot comes back as a tree hash and a diff.</summary>
public sealed record MergePreviewRequest
{
    /// <summary>Branch that would receive the merge: "ours" when <see cref="Ours"/> is null, and the target for approval rules.</summary>
    public string? Branch { get; init; }
    /// <summary>Any revision to merge into instead of the branch head (e.g. an older point on main).</summary>
    public string? Ours { get; init; }
    public string Theirs { get; init; } = "";
    public List<MergeResolution>? Resolutions { get; init; }
    public List<MergeRule>? Rules { get; init; }
    public bool? UseRepoRules { get; init; }
    /// <summary>Revision that <c>changes</c> are relative to (default: ours). Clients pass the commit their document is at.</summary>
    public string? CompareTo { get; init; }
    /// <summary>Include manifests in <c>changes</c>.</summary>
    public bool Manifests { get; init; }
    /// <summary>Who would merge; decides which approvals are required.</summary>
    public AuthorInfo? Author { get; init; }
    public bool NoFastForward { get; init; }
}

public sealed record DiffSummary
{
    public int Added { get; init; }
    public int Modified { get; init; }
    public int Deleted { get; init; }
}

public sealed record MergePreviewResponse
{
    /// <summary>"upToDate", "fastForward", "clean" (three-way, nothing unresolved) or "conflicts".</summary>
    public string Status { get; init; } = "";
    public string Ours { get; init; } = "";
    public string Theirs { get; init; } = "";
    public string? MergeBase { get; init; }
    /// <summary>Root of the merged snapshot. Unresolved conflicts keep our version in it.</summary>
    public string Tree { get; init; } = "";
    /// <summary>True when a merge with the same inputs would succeed (approval aside).</summary>
    public bool CanMerge { get; init; }
    public bool RequiresApproval { get; init; }
    /// <summary>"protected_branch", "rules_applied", "not_branch_owner".</summary>
    public List<string> ApprovalReasons { get; init; } = [];
    public List<MergeConflict> Conflicts { get; init; } = [];
    public List<AutoResolution> AutoResolved { get; init; } = [];
    /// <summary>Invariant violations of the merged snapshot (a merge would fail with 422).</summary>
    public List<ApiErrorDetail> Validation { get; init; } = [];
    /// <summary>What the merge changes on "ours".</summary>
    public DiffSummary Incoming { get; init; } = new();
    public string CompareTo { get; init; } = "";
    /// <summary>Entity changes from <see cref="CompareTo"/> to the merged snapshot.</summary>
    public List<DiffEntry> Changes { get; init; } = [];
}

public sealed record MergeBaseResponse
{
    public string A { get; init; } = "";
    public string B { get; init; } = "";
    public string? MergeBase { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Branches, policy and approvals
// ---------------------------------------------------------------------------------------------

/// <summary>A branch with the metadata the server records when it is created.</summary>
public sealed record BranchInfo
{
    public string Name { get; init; } = "";
    public string Commit { get; init; } = "";
    /// <summary>The branch this one was split from; MERGE targets it by default.</summary>
    public string? Parent { get; init; }
    /// <summary>The commit the branch was created at (null when its first commit created it).</summary>
    public string? Base { get; init; }
    public AuthorInfo? CreatedBy { get; init; }
    public DateTimeOffset? Created { get; init; }
    public bool Protected { get; init; }
}

public sealed record BranchesResponse
{
    public List<BranchInfo> Branches { get; init; } = [];
}

public sealed record CreateBranchRequest
{
    public string Name { get; init; } = "";
    /// <summary>Revision to start at (commit, branch or tag).</summary>
    public string From { get; init; } = Names.DefaultBranch;
    /// <summary>Default: <see cref="From"/> when it names a branch.</summary>
    public string? Parent { get; init; }
    public AuthorInfo Author { get; init; } = new();
}

public sealed record RepoPolicy
{
    /// <summary>Agents cannot commit to these branches, and every merge into them needs a human approval.</summary>
    public List<string> ProtectedBranches { get; init; } = [Names.DefaultBranch];
    public List<MergeRule> MergeRules { get; init; } = [];
}

public sealed record PolicyUpdateRequest
{
    public List<string> ProtectedBranches { get; init; } = [Names.DefaultBranch];
    public List<MergeRule> MergeRules { get; init; } = [];
    /// <summary>Must be a human.</summary>
    public AuthorInfo Author { get; init; } = new();
}

/// <summary>A human's approval to merge one exact pair of heads. Stale as soon as either side moves; used once.</summary>
public sealed record ApprovalRequest
{
    public string Branch { get; init; } = Names.DefaultBranch;
    public string Theirs { get; init; } = "";
    public AuthorInfo By { get; init; } = new();
    public string? Note { get; init; }
}

public sealed record ApprovalInfo
{
    public string Id { get; init; } = "";
    public string Branch { get; init; } = "";
    /// <summary>Branch head and theirs commit at approval time.</summary>
    public string Ours { get; init; } = "";
    public string Theirs { get; init; } = "";
    /// <summary>"theirs" as the approver named it (usually a branch).</summary>
    public string TheirsRef { get; init; } = "";
    public AuthorInfo By { get; init; } = new();
    public string? Note { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    /// <summary>"active", "used" or "stale" (computed when read).</summary>
    public string Status { get; init; } = ApprovalStatuses.Active;
    /// <summary>The commit the branch moved to with this approval.</summary>
    public string? UsedBy { get; init; }
}

public sealed record ApprovalsResponse
{
    public List<ApprovalInfo> Approvals { get; init; } = [];
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
