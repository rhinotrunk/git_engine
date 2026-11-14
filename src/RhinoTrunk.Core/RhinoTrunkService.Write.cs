using RhinoTrunk.Contracts;
using RhinoTrunk.Core.Storage;

namespace RhinoTrunk.Core;

public sealed partial class RhinoTrunkService
{
    // =========================================================================================
    // Commit
    // =========================================================================================

    /// <summary>
    /// Applies a changeset on top of the branch head the client expects, validates the resulting snapshot
    /// and advances the branch with compare-and-swap. Nothing reaches the branch unless it is valid.
    /// </summary>
    public async Task<CommitResponse> CommitAsync(string repo, CommitRequest request, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var shapeErrors = ValidateShape(request);
        if (shapeErrors.Count > 0)
            throw RhinoTrunkException.BadRequest("The commit request is malformed.", shapeErrors);

        await ctx.WriteLock.WaitAsync(ct);
        try
        {
            var fullRef = HeadsPrefix + request.Branch;
            var head = await ctx.Store.Refs.GetAsync(fullRef, ct);
            if (head != request.ExpectedHead) throw RefConflict(fullRef, head);
            var parent = head is null ? null : await ctx.RequireCommitAsync(head, ct);
            var baseRoot = parent?.Tree ?? await ctx.Trees.EmptyRootAsync(ct);

            var errors = new List<ApiErrorDetail>();
            var resolved = new List<ResolvedChange>();
            var versions = new List<EntityVersion>();
            foreach (var change in request.Changes)
            {
                var key = new EntityKey(change.Doc, change.Kind, change.Id);
                if (change.Op == ChangeOps.Delete)
                {
                    if (await ctx.Trees.GetAsync(baseRoot, key, ct) is null)
                        errors.Add(Detail(ErrorCodes.EntityNotFound, $"Cannot delete {key.Kind} {key.Id}: it does not exist.", key));
                    resolved.Add(new ResolvedChange(key, null, null));
                    continue;
                }

                Manifest? manifest;
                string version;
                if (change.Version is not null)
                {
                    version = change.Version;
                    manifest = await ctx.Manifests.GetAsync(version, ct);
                    if (manifest is null)
                    {
                        errors.Add(Detail(ErrorCodes.ObjectMissing, $"Version {version} is not a stored manifest.", key));
                        continue;
                    }
                }
                else
                {
                    (manifest, version) = await StoreManifestAsync(ctx, change, errors, ct);
                }
                resolved.Add(new ResolvedChange(key, version, manifest));
                versions.Add(new EntityVersion { Doc = key.Doc, Kind = key.Kind, Id = key.Id, Version = version });
            }
            if (errors.Count > 0)
                throw RhinoTrunkException.Unprocessable(ErrorCodes.ValidationFailed, "The commit was rejected.", errors);

            var newRoot = await ctx.Trees.ApplyAsync(baseRoot, resolved.Select(r => new TreeChange(r.Key, r.Version)), ct);
            if (newRoot == baseRoot)
                throw RhinoTrunkException.Unprocessable(ErrorCodes.NoChanges, "The changes do not alter the snapshot.");

            errors = await ctx.Validator.ValidateAsync(newRoot, resolved, ct);
            if (errors.Count > 0)
                throw RhinoTrunkException.Unprocessable(ErrorCodes.ValidationFailed, "The resulting snapshot violates document invariants.", errors);

            var commit = new CommitObject
            {
                Tree = newRoot,
                Parents = head is null ? [] : [head],
                Generation = (parent?.Generation ?? 0) + 1,
                Author = request.Author,
                Timestamp = Now(),
                Message = request.Message,
                Meta = request.Meta is { Count: > 0 } ? request.Meta : null,
            };
            var commitId = await ctx.Store.Objects.PutJsonAsync(commit, ct);
            var log = new RefLogEntry(fullRef, head, commitId, request.Author.Id, commit.Timestamp, "commit: " + FirstLine(request.Message));
            if (!await ctx.Store.Refs.CompareAndSwapAsync(fullRef, head, commitId, log, ct))
                throw RefConflict(fullRef, await ctx.Store.Refs.GetAsync(fullRef, ct));

            return new CommitResponse { Commit = commitId, Tree = newRoot, Branch = request.Branch, Versions = versions };
        }
        finally
        {
            ctx.WriteLock.Release();
        }
    }

    private static async Task<(Manifest, string)> StoreManifestAsync(RepoContext ctx, Change change, List<ApiErrorDetail> errors, CancellationToken ct)
    {
        var input = change.Manifest!;
        var key = new EntityKey(change.Doc, change.Kind, change.Id);
        var components = new Dictionary<string, string>(input.Components ?? [], StringComparer.Ordinal);
        if (input.Attributes is not null)
        {
            if (components.ContainsKey("attributes"))
            {
                errors.Add(Detail(ErrorCodes.InvalidManifest, "Attributes were given both inline and as a component.", key));
            }
            else
            {
                var bytes = CanonicalJson.Serialize(input.Attributes);
                var hash = ContentHash.Compute(bytes);
                await ctx.Store.Objects.PutAsync(hash, bytes, ct);
                components["attributes"] = hash;
            }
        }
        var refs = (input.Refs ?? [])
            .DistinctBy(r => (r.Kind, r.Id, r.Role))
            .OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Role, StringComparer.Ordinal)
            .ToList();
        var manifest = new Manifest
        {
            Kind = input.Kind ?? change.Kind,
            Subtype = string.IsNullOrEmpty(input.Subtype) ? null : input.Subtype,
            Components = components,
            Refs = refs,
        };
        var version = await ctx.Store.Objects.PutJsonAsync(manifest, ct);
        return (manifest, version);
    }

    private static List<ApiErrorDetail> ValidateShape(CommitRequest request)
    {
        var errors = new List<ApiErrorDetail>();
        void Add(string code, string message, Change? c = null) =>
            errors.Add(new ApiErrorDetail { Code = code, Message = message, Doc = c?.Doc, Kind = c?.Kind, Id = c?.Id });

        if (!Names.IsValidRefName(request.Branch)) Add(ErrorCodes.InvalidRequest, $"Invalid branch name '{request.Branch}'.");
        if (request.ExpectedHead is not null && !ContentHash.IsValid(request.ExpectedHead)) Add(ErrorCodes.InvalidRequest, "expectedHead must be a commit hash or null.");
        CheckAuthor(request.Author, Add);
        if (request.Changes is not { Count: > 0 }) Add(ErrorCodes.InvalidRequest, "A commit needs at least one change.");

        var seen = new HashSet<(string, string, string)>();
        foreach (var c in request.Changes ?? [])
        {
            if (c is null) { Add(ErrorCodes.InvalidRequest, "Null change."); continue; }
            if (c.Op is not (ChangeOps.Put or ChangeOps.Delete)) Add(ErrorCodes.InvalidRequest, $"Unknown op '{c.Op}'.", c);
            if (!Names.IsValidDoc(c.Doc)) Add(ErrorCodes.InvalidRequest, $"Invalid document id '{c.Doc}'.", c);
            if (!EntityKinds.IsKnown(c.Kind)) Add(ErrorCodes.UnknownKind, $"Unknown kind '{c.Kind}'.", c);
            else if (!Names.IsValidEntityId(c.Kind, c.Id)) Add(ErrorCodes.InvalidEntityId, $"Invalid id '{c.Id}' for kind '{c.Kind}' (lowercase GUID; 'settings' for the settings entity).", c);
            if (!seen.Add((c.Doc, c.Kind, c.Id))) Add(ErrorCodes.DuplicateChange, "The same entity appears twice in one commit.", c);
            if (c.Op == ChangeOps.Put && (c.Manifest is null) == (c.Version is null)) Add(ErrorCodes.InvalidRequest, "A put needs exactly one of 'manifest' or 'version'.", c);
            if (c.Op == ChangeOps.Put && c.Version is not null && !ContentHash.IsValid(c.Version)) Add(ErrorCodes.InvalidRequest, "Invalid version hash.", c);
            if (c.Op == ChangeOps.Delete && (c.Manifest is not null || c.Version is not null)) Add(ErrorCodes.InvalidRequest, "A delete carries no manifest or version.", c);
            if (c.Manifest?.Kind is { } k && k != c.Kind) Add(ErrorCodes.KindMismatch, $"Manifest kind '{k}' does not match change kind '{c.Kind}'.", c);
        }
        return errors;
    }

    private static void CheckAuthor(AuthorInfo? author, Action<string, string, Change?> add)
    {
        if (author is null || string.IsNullOrWhiteSpace(author.Id)) add(ErrorCodes.InvalidRequest, "author.id is required.", null);
        else if (author.Kind is not (AuthorKinds.Human or AuthorKinds.Agent)) add(ErrorCodes.InvalidRequest, "author.kind must be 'human' or 'agent'.", null);
    }

    private static ApiErrorDetail Detail(string code, string message, EntityKey key) =>
        new() { Code = code, Message = message, Doc = key.Doc, Kind = key.Kind, Id = key.Id };

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 80 ? line[..80] : line;
    }

    // =========================================================================================
    // Merge
    // =========================================================================================

    /// <summary>
    /// Three-way merge at entity level, then component level. Disjoint edits combine automatically
    /// (e.g. geometry changed on one side, attributes on the other). The merged snapshot passes the same
    /// validation as any commit, which catches semantic conflicts such as a layer deleted on one side
    /// and used by new objects on the other.
    /// </summary>
    public async Task<MergeResponse> MergeAsync(string repo, MergeRequest request, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var shape = new List<ApiErrorDetail>();
        void Add(string code, string message, Change? _) => shape.Add(new ApiErrorDetail { Code = code, Message = message });
        if (!Names.IsValidRefName(request.Branch)) Add(ErrorCodes.InvalidRequest, $"Invalid branch name '{request.Branch}'.", null);
        if (string.IsNullOrWhiteSpace(request.Theirs)) Add(ErrorCodes.InvalidRequest, "theirs is required.", null);
        CheckAuthor(request.Author, Add);
        foreach (var r in request.Resolutions ?? [])
            if (r.Take is not (MergeTakes.Ours or MergeTakes.Theirs)) Add(ErrorCodes.InvalidRequest, "Resolution 'take' must be 'ours' or 'theirs'.", null);
        if (shape.Count > 0) throw RhinoTrunkException.BadRequest("The merge request is malformed.", shape);

        await ctx.WriteLock.WaitAsync(ct);
        try
        {
            var fullRef = HeadsPrefix + request.Branch;
            var ours = await ctx.Store.Refs.GetAsync(fullRef, ct) ?? throw RhinoTrunkException.NotFound($"Branch '{request.Branch}' not found.");
            if (request.ExpectedHead is not null && request.ExpectedHead != ours) throw RefConflict(fullRef, ours);
            var theirs = await ResolveAsync(ctx, request.Theirs, ct);

            if (await IsAncestorAsync(ctx, theirs, ours, ct))
                return new MergeResponse { Status = MergeStatuses.UpToDate, Commit = ours, MergeBase = theirs, Branch = request.Branch };

            if (!request.NoFastForward && await IsAncestorAsync(ctx, ours, theirs, ct))
            {
                var ffLog = new RefLogEntry(fullRef, ours, theirs, request.Author.Id, Now(), "merge: fast-forward to " + request.Theirs);
                if (!await ctx.Store.Refs.CompareAndSwapAsync(fullRef, ours, theirs, ffLog, ct))
                    throw RefConflict(fullRef, await ctx.Store.Refs.GetAsync(fullRef, ct));
                return new MergeResponse { Status = MergeStatuses.FastForward, Commit = theirs, MergeBase = ours, Branch = request.Branch };
            }

            var mergeBase = await MergeBaseAsync(ctx, ours, theirs, ct);
            var baseTree = mergeBase is null ? await ctx.Trees.EmptyRootAsync(ct) : (await ctx.RequireCommitAsync(mergeBase, ct)).Tree;
            var oursCommit = await ctx.RequireCommitAsync(ours, ct);
            var theirsCommit = await ctx.RequireCommitAsync(theirs, ct);

            var oursDiff = (await ctx.Trees.DiffAsync(baseTree, oursCommit.Tree, ct)).ToDictionary(d => d.Key);
            var theirsDiff = await ctx.Trees.DiffAsync(baseTree, theirsCommit.Tree, ct);
            var resolutions = new Dictionary<EntityKey, string>();
            foreach (var r in request.Resolutions ?? []) resolutions[new EntityKey(r.Doc, r.Kind, r.Id)] = r.Take; // last one wins

            var changes = new List<ResolvedChange>();
            var conflicts = new List<ApiErrorDetail>();
            foreach (var t in theirsDiff)
            {
                if (!oursDiff.TryGetValue(t.Key, out var o))
                {
                    changes.Add(await ResolveChangeAsync(ctx, t.Key, t.To, ct));
                    continue;
                }
                if (o.To == t.To) continue; // both sides made the same change

                if (resolutions.TryGetValue(t.Key, out var take))
                {
                    if (take == MergeTakes.Theirs) changes.Add(await ResolveChangeAsync(ctx, t.Key, t.To, ct));
                    continue;
                }

                if (t.From is not null && o.To is not null && t.To is not null)
                {
                    var b = await ctx.Manifests.GetAsync(t.From, ct);
                    var om = await ctx.Manifests.GetAsync(o.To, ct);
                    var tm = await ctx.Manifests.GetAsync(t.To, ct);
                    if (b is not null && om is not null && tm is not null)
                    {
                        var (merged, fields) = MergeManifests(b, om, tm);
                        if (merged is not null)
                        {
                            var version = await ctx.Store.Objects.PutJsonAsync(merged, ct);
                            if (version != o.To) changes.Add(new ResolvedChange(t.Key, version, merged));
                            continue;
                        }
                        conflicts.Add(Conflict(t.Key, "Both sides changed the same fields.", fields));
                        continue;
                    }
                }
                var reason = t.From is null ? "Added on both sides with different content."
                    : o.To is null ? "Deleted on our side, modified on theirs."
                    : "Modified on our side, deleted on theirs.";
                conflicts.Add(Conflict(t.Key, reason, null));
            }
            if (conflicts.Count > 0)
                throw RhinoTrunkException.Conflict(ErrorCodes.MergeConflict,
                    $"{conflicts.Count} conflicting entities. Resolve them with 'resolutions' (ours/theirs) or commit fixes and merge again.",
                    new Dictionary<string, string> { ["mergeBase"] = mergeBase ?? "", ["ours"] = ours, ["theirs"] = theirs },
                    conflicts);

            var newRoot = changes.Count == 0
                ? oursCommit.Tree
                : await ctx.Trees.ApplyAsync(oursCommit.Tree, changes.Select(c => new TreeChange(c.Key, c.Version)), ct);
            var errors = await ctx.Validator.ValidateAsync(newRoot, changes, ct);
            if (errors.Count > 0)
                throw RhinoTrunkException.Unprocessable(ErrorCodes.ValidationFailed, "The merged snapshot violates document invariants.", errors);

            var commit = new CommitObject
            {
                Tree = newRoot,
                Parents = [ours, theirs],
                Generation = Math.Max(oursCommit.Generation, theirsCommit.Generation) + 1,
                Author = request.Author,
                Timestamp = Now(),
                Message = string.IsNullOrWhiteSpace(request.Message) ? $"Merge {request.Theirs} into {request.Branch}" : request.Message,
            };
            var commitId = await ctx.Store.Objects.PutJsonAsync(commit, ct);
            var log = new RefLogEntry(fullRef, ours, commitId, request.Author.Id, commit.Timestamp, "merge: " + request.Theirs);
            if (!await ctx.Store.Refs.CompareAndSwapAsync(fullRef, ours, commitId, log, ct))
                throw RefConflict(fullRef, await ctx.Store.Refs.GetAsync(fullRef, ct));
            return new MergeResponse { Status = MergeStatuses.Merged, Commit = commitId, MergeBase = mergeBase, Branch = request.Branch };
        }
        finally
        {
            ctx.WriteLock.Release();
        }
    }

    private static async Task<ResolvedChange> ResolveChangeAsync(RepoContext ctx, EntityKey key, string? version, CancellationToken ct) =>
        new(key, version, version is null ? null : await ctx.Manifests.GetAsync(version, ct));

    private static ApiErrorDetail Conflict(EntityKey key, string message, List<string>? fields) =>
        new() { Code = ErrorCodes.Conflict, Message = message, Doc = key.Doc, Kind = key.Kind, Id = key.Id, Fields = fields };

    /// <summary>Three-way merge of two manifests field by field (subtype, refs, each component).</summary>
    public static (Manifest? Merged, List<string> ConflictingFields) MergeManifests(Manifest b, Manifest ours, Manifest theirs)
    {
        var conflicts = new List<string>();
        T? Pick<T>(string field, T? bv, T? ov, T? tv, Func<T?, T?, bool> eq)
        {
            if (eq(ov, tv)) return ov;
            if (eq(ov, bv)) return tv;
            if (eq(tv, bv)) return ov;
            conflicts.Add(field);
            return ov;
        }

        if (ours.Kind != theirs.Kind) conflicts.Add("kind");
        var subtype = Pick("subtype", b.Subtype, ours.Subtype, theirs.Subtype, string.Equals);
        var refs = Pick("refs", b.Refs, ours.Refs, theirs.Refs, (x, y) => (x ?? []).SequenceEqual(y ?? []));
        var components = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in b.Components.Keys.Union(ours.Components.Keys).Union(theirs.Components.Keys).OrderBy(n => n, StringComparer.Ordinal))
        {
            var value = Pick("components." + name, b.Components.GetValueOrDefault(name), ours.Components.GetValueOrDefault(name),
                theirs.Components.GetValueOrDefault(name), string.Equals);
            if (value is not null) components[name] = value;
        }
        if (conflicts.Count > 0) return (null, conflicts);
        return (new Manifest { Kind = ours.Kind, Subtype = subtype, Components = components, Refs = refs ?? [] }, conflicts);
    }
}
