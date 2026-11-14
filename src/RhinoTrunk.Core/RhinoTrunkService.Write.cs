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
        if (request.Author.Kind == AuthorKinds.Agent && await IsProtectedAsync(ctx, request.Branch, ct))
            throw Forbidden(ErrorCodes.Forbidden,
                $"Agents cannot commit to the protected branch '{request.Branch}'. Commit to your own branch (e.g. agent/<task>) and ask a human to merge.");

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
            if (head is null)
                await RecordBranchAsync(ctx, request.Branch, request.Parent, null, request.Author, ct);

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
    /// (e.g. geometry changed on one side, attributes on the other); merge rules and explicit resolutions
    /// settle the rest. The merged snapshot passes the same validation as any commit, which catches semantic
    /// conflicts such as a layer deleted on one side and used by new objects on the other.
    /// Merges into protected branches, rule-resolved merges and agent merges into foreign branches need a
    /// human approval (see <see cref="CheckApprovalAsync"/>).
    /// </summary>
    public async Task<MergeResponse> MergeAsync(string repo, MergeRequest request, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var shape = new List<ApiErrorDetail>();
        void Add(string code, string message, Change? _) => shape.Add(new ApiErrorDetail { Code = code, Message = message });
        if (!Names.IsValidRefName(request.Branch)) Add(ErrorCodes.InvalidRequest, $"Invalid branch name '{request.Branch}'.", null);
        if (string.IsNullOrWhiteSpace(request.Theirs)) Add(ErrorCodes.InvalidRequest, "theirs is required.", null);
        CheckAuthor(request.Author, Add);
        CheckResolutions(request.Resolutions, Add);
        shape.AddRange(CheckRules(request.Rules));
        if (shape.Count > 0) throw RhinoTrunkException.BadRequest("The merge request is malformed.", shape);

        await ctx.WriteLock.WaitAsync(ct);
        try
        {
            var fullRef = HeadsPrefix + request.Branch;
            var ours = await ctx.Store.Refs.GetAsync(fullRef, ct) ?? throw RhinoTrunkException.NotFound($"Branch '{request.Branch}' not found.");
            if (request.ExpectedHead is not null && request.ExpectedHead != ours) throw RefConflict(fullRef, ours);
            var theirs = await ResolveAsync(ctx, request.Theirs, ct);
            var rules = await RulesForAsync(ctx, request.UseRepoRules, request.Rules, ct);
            var plan = await PlanMergeAsync(ctx, ours, theirs, request.Resolutions, rules, request.NoFastForward, ct);

            if (plan.Status == MergePreviewStatuses.UpToDate)
                return new MergeResponse { Status = MergeStatuses.UpToDate, Commit = ours, MergeBase = theirs, Branch = request.Branch };

            if (plan.Conflicts.Count > 0)
                throw RhinoTrunkException.Conflict(ErrorCodes.MergeConflict,
                    $"{plan.Conflicts.Count} conflicting entities. Resolve them with 'resolutions' (ours/theirs), merge rules, or commit fixes and merge again.",
                    new Dictionary<string, string> { ["mergeBase"] = plan.MergeBase ?? "", ["ours"] = ours, ["theirs"] = theirs },
                    plan.Conflicts.Select(c => new ApiErrorDetail
                    {
                        Code = ErrorCodes.Conflict, Message = c.Reason, Doc = c.Doc, Kind = c.Kind, Id = c.Id, Fields = c.Fields,
                    }).ToList());

            if (plan.Status == MergePreviewStatuses.Clean)
            {
                var errors = await ctx.Validator.ValidateAsync(plan.Tree, plan.Changes, ct);
                if (errors.Count > 0)
                    throw RhinoTrunkException.Unprocessable(ErrorCodes.ValidationFailed, "The merged snapshot violates document invariants.", errors);
            }

            var reasons = await ApprovalReasonsAsync(ctx, request.Branch, request.Author, plan.AutoResolved.Count > 0, ct);
            var (approvedBy, approval) = await CheckApprovalAsync(ctx, request, ours, theirs, reasons, plan.AutoResolved.Count, ct);
            var autoResolved = plan.AutoResolved.Count > 0 ? plan.AutoResolved : null;

            if (plan.Status == MergePreviewStatuses.FastForward)
            {
                var ffLog = new RefLogEntry(fullRef, ours, theirs, request.Author.Id, Now(),
                    "merge: fast-forward to " + request.Theirs + (approvedBy is null ? "" : " (approved by " + approvedBy.Id + ")"));
                if (!await ctx.Store.Refs.CompareAndSwapAsync(fullRef, ours, theirs, ffLog, ct))
                    throw RefConflict(fullRef, await ctx.Store.Refs.GetAsync(fullRef, ct));
                await MarkApprovalUsedAsync(ctx, approval, theirs, ct);
                return new MergeResponse { Status = MergeStatuses.FastForward, Commit = theirs, MergeBase = ours, Branch = request.Branch, ApprovedBy = approvedBy };
            }

            var oursCommit = await ctx.RequireCommitAsync(ours, ct);
            var theirsCommit = await ctx.RequireCommitAsync(theirs, ct);
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            if (approvedBy is not null) meta["merge.approvedBy"] = approvedBy.Id;
            if (approval is not null) meta["merge.approval"] = approval.Id;
            if (autoResolved is not null)
            {
                meta["merge.autoResolved"] = autoResolved.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                meta["merge.rules"] = string.Join(",", autoResolved.Select(a => a.Rule).Distinct(StringComparer.Ordinal));
            }
            var commit = new CommitObject
            {
                Tree = plan.Tree,
                Parents = [ours, theirs],
                Generation = Math.Max(oursCommit.Generation, theirsCommit.Generation) + 1,
                Author = request.Author,
                Timestamp = Now(),
                Message = string.IsNullOrWhiteSpace(request.Message) ? $"Merge {request.Theirs} into {request.Branch}" : request.Message,
                Meta = meta.Count > 0 ? meta : null,
            };
            var commitId = await ctx.Store.Objects.PutJsonAsync(commit, ct);
            var log = new RefLogEntry(fullRef, ours, commitId, request.Author.Id, commit.Timestamp, "merge: " + request.Theirs);
            if (!await ctx.Store.Refs.CompareAndSwapAsync(fullRef, ours, commitId, log, ct))
                throw RefConflict(fullRef, await ctx.Store.Refs.GetAsync(fullRef, ct));
            await MarkApprovalUsedAsync(ctx, approval, commitId, ct);
            return new MergeResponse
            {
                Status = MergeStatuses.Merged, Commit = commitId, MergeBase = plan.MergeBase, Branch = request.Branch,
                AutoResolved = autoResolved, ApprovedBy = approvedBy,
            };
        }
        finally
        {
            ctx.WriteLock.Release();
        }
    }

    /// <summary>
    /// Dry run of <see cref="MergeAsync"/> into a branch head or any other revision (e.g. a point on main).
    /// Moves no ref and needs no lock: it only writes content-addressed manifests and tree nodes. Returns the
    /// merged tree, conflicts with all three versions, rule resolutions, approval requirements and the
    /// entity changes from <see cref="MergePreviewRequest.CompareTo"/> (the client's document) to the result.
    /// </summary>
    public async Task<MergePreviewResponse> PreviewMergeAsync(string repo, MergePreviewRequest request, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var shape = new List<ApiErrorDetail>();
        void Add(string code, string message, Change? _) => shape.Add(new ApiErrorDetail { Code = code, Message = message });
        if (request.Branch is not null && !Names.IsValidRefName(request.Branch)) Add(ErrorCodes.InvalidRequest, $"Invalid branch name '{request.Branch}'.", null);
        if (string.IsNullOrWhiteSpace(request.Theirs)) Add(ErrorCodes.InvalidRequest, "theirs is required.", null);
        if (request.Author is not null) CheckAuthor(request.Author, Add);
        CheckResolutions(request.Resolutions, Add);
        shape.AddRange(CheckRules(request.Rules));
        if (shape.Count > 0) throw RhinoTrunkException.BadRequest("The preview request is malformed.", shape);

        var branch = request.Branch ?? (request.Ours is null ? Names.DefaultBranch : null);
        string ours;
        if (request.Ours is not null) ours = await ResolveAsync(ctx, request.Ours, ct);
        else ours = await ctx.Store.Refs.GetAsync(HeadsPrefix + branch, ct) ?? throw RhinoTrunkException.NotFound($"Branch '{branch}' not found.");
        var theirs = await ResolveAsync(ctx, request.Theirs, ct);
        var rules = await RulesForAsync(ctx, request.UseRepoRules, request.Rules, ct);
        var plan = await PlanMergeAsync(ctx, ours, theirs, request.Resolutions, rules, request.NoFastForward, ct);

        var compareTo = request.CompareTo is null ? ours : await ResolveAsync(ctx, request.CompareTo, ct);
        var compareTree = (await ctx.RequireCommitAsync(compareTo, ct)).Tree;
        var changes = new List<DiffEntry>();
        foreach (var d in await ctx.Trees.DiffAsync(compareTree, plan.Tree, ct))
            changes.Add(await DescribeAsync(ctx, d, request.Manifests, ct));
        var incoming = await ctx.Trees.DiffAsync(plan.OursTree, plan.Tree, ct);

        var validation = plan.Status == MergePreviewStatuses.Clean && plan.Changes.Count > 0
            ? await ctx.Validator.ValidateAsync(plan.Tree, plan.Changes, ct)
            : [];
        var reasons = plan.Status == MergePreviewStatuses.UpToDate
            ? []
            : await ApprovalReasonsAsync(ctx, branch, request.Author, plan.AutoResolved.Count > 0, ct);
        return new MergePreviewResponse
        {
            Status = plan.Status, Ours = ours, Theirs = theirs, MergeBase = plan.MergeBase, Tree = plan.Tree,
            CanMerge = plan.Status is MergePreviewStatuses.FastForward or MergePreviewStatuses.Clean && validation.Count == 0,
            RequiresApproval = reasons.Count > 0, ApprovalReasons = reasons,
            Conflicts = plan.Conflicts, AutoResolved = plan.AutoResolved, Validation = validation,
            Incoming = new DiffSummary
            {
                Added = incoming.Count(d => d.From is null),
                Modified = incoming.Count(d => d.From is not null && d.To is not null),
                Deleted = incoming.Count(d => d.To is null),
            },
            CompareTo = compareTo, Changes = changes,
        };
    }

    private static void CheckResolutions(List<MergeResolution>? resolutions, Action<string, string, Change?> add)
    {
        foreach (var r in resolutions ?? [])
            if (r.Take is not (MergeTakes.Ours or MergeTakes.Theirs)) add(ErrorCodes.InvalidRequest, "Resolution 'take' must be 'ours' or 'theirs'.", null);
    }

    /// <summary>Result of planning a merge: what would change on "ours", and what is still in conflict.</summary>
    internal sealed record MergePlan(
        string Status, string Ours, string Theirs, string? MergeBase, string OursTree, string Tree,
        List<ResolvedChange> Changes, List<MergeConflict> Conflicts, List<AutoResolution> AutoResolved);

    private static async Task<MergePlan> PlanMergeAsync(RepoContext ctx, string ours, string theirs, List<MergeResolution>? requestResolutions,
        List<MergeRule> rules, bool noFastForward, CancellationToken ct)
    {
        var oursCommit = await ctx.RequireCommitAsync(ours, ct);
        var theirsCommit = await ctx.RequireCommitAsync(theirs, ct);
        if (await IsAncestorAsync(ctx, theirs, ours, ct))
            return new MergePlan(MergePreviewStatuses.UpToDate, ours, theirs, theirs, oursCommit.Tree, oursCommit.Tree, [], [], []);
        if (!noFastForward && await IsAncestorAsync(ctx, ours, theirs, ct))
            return new MergePlan(MergePreviewStatuses.FastForward, ours, theirs, ours, oursCommit.Tree, theirsCommit.Tree, [], [], []);

        var mergeBase = await MergeBaseAsync(ctx, ours, theirs, ct);
        var baseTree = mergeBase is null ? await ctx.Trees.EmptyRootAsync(ct) : (await ctx.RequireCommitAsync(mergeBase, ct)).Tree;
        var oursDiff = (await ctx.Trees.DiffAsync(baseTree, oursCommit.Tree, ct)).ToDictionary(d => d.Key);
        var theirsDiff = await ctx.Trees.DiffAsync(baseTree, theirsCommit.Tree, ct);
        var resolutions = new Dictionary<EntityKey, string>();
        foreach (var r in requestResolutions ?? []) resolutions[new EntityKey(r.Doc, r.Kind, r.Id)] = r.Take; // last one wins

        var changes = new List<ResolvedChange>();
        var conflicts = new List<MergeConflict>();
        var autoResolved = new List<AutoResolution>();
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
                    if (merged is null && MatchRule(rules, t.Key.Kind, fields) is { } rule)
                    {
                        (merged, _) = MergeManifests(b, om, tm, rule.Take);
                        autoResolved.Add(new AutoResolution { Doc = t.Key.Doc, Kind = t.Key.Kind, Id = t.Key.Id, Fields = fields, Take = rule.Take, Rule = rule.Name });
                    }
                    if (merged is not null)
                    {
                        var version = await ctx.Store.Objects.PutJsonAsync(merged, ct);
                        if (version != o.To) changes.Add(new ResolvedChange(t.Key, version, merged));
                        continue;
                    }
                    conflicts.Add(new MergeConflict
                    {
                        Doc = t.Key.Doc, Kind = t.Key.Kind, Id = t.Key.Id, Reason = "Both sides changed the same fields.",
                        Fields = fields, Base = t.From, Ours = o.To, Theirs = t.To,
                    });
                    continue;
                }
            }
            var reason = t.From is null ? "Added on both sides with different content."
                : o.To is null ? "Deleted on our side, modified on theirs."
                : "Modified on our side, deleted on theirs.";
            if (MatchRule(rules, t.Key.Kind, null) is { } whole)
            {
                if (whole.Take == MergeTakes.Theirs) changes.Add(await ResolveChangeAsync(ctx, t.Key, t.To, ct));
                autoResolved.Add(new AutoResolution { Doc = t.Key.Doc, Kind = t.Key.Kind, Id = t.Key.Id, Take = whole.Take, Rule = whole.Name });
                continue;
            }
            conflicts.Add(new MergeConflict { Doc = t.Key.Doc, Kind = t.Key.Kind, Id = t.Key.Id, Reason = reason, Base = t.From, Ours = o.To, Theirs = t.To });
        }

        var tree = changes.Count == 0
            ? oursCommit.Tree
            : await ctx.Trees.ApplyAsync(oursCommit.Tree, changes.Select(c => new TreeChange(c.Key, c.Version)), ct);
        var status = conflicts.Count > 0 ? MergePreviewStatuses.Conflicts : MergePreviewStatuses.Clean;
        return new MergePlan(status, ours, theirs, mergeBase, oursCommit.Tree, tree, changes, conflicts, autoResolved);
    }

    /// <summary>
    /// First rule that resolves a conflict on <paramref name="kind"/>. <paramref name="fields"/> null means a whole-entity
    /// conflict (add/add, modify/delete), which only rules without a field list resolve.
    /// </summary>
    private static MergeRule? MatchRule(List<MergeRule> rules, string kind, List<string>? fields) =>
        rules.FirstOrDefault(r => (r.Kind is null || r.Kind == kind)
                                  && (fields is null ? r.Fields is null : r.Fields is null || fields.All(f => r.Fields.Contains(f))));

    private static async Task<ResolvedChange> ResolveChangeAsync(RepoContext ctx, EntityKey key, string? version, CancellationToken ct) =>
        new(key, version, version is null ? null : await ctx.Manifests.GetAsync(version, ct));

    /// <summary>
    /// Three-way merge of two manifests field by field (subtype, refs, each component). With <paramref name="prefer"/>
    /// ("ours"/"theirs") conflicting fields take that side and a merged manifest is always returned; the conflicting
    /// fields are still listed.
    /// </summary>
    public static (Manifest? Merged, List<string> ConflictingFields) MergeManifests(Manifest b, Manifest ours, Manifest theirs, string? prefer = null)
    {
        var conflicts = new List<string>();
        T? Pick<T>(string field, T? bv, T? ov, T? tv, Func<T?, T?, bool> eq)
        {
            if (eq(ov, tv)) return ov;
            if (eq(ov, bv)) return tv;
            if (eq(tv, bv)) return ov;
            conflicts.Add(field);
            return prefer == MergeTakes.Theirs ? tv : ov;
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
        if (conflicts.Count > 0 && (prefer is null || conflicts.Contains("kind"))) return (null, conflicts);
        return (new Manifest { Kind = ours.Kind, Subtype = subtype, Components = components, Refs = refs ?? [] }, conflicts);
    }
}
