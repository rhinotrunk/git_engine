using System.Text.Json;
using RhinoTrunk.Contracts;
using RhinoTrunk.Core.Storage;

namespace RhinoTrunk.Core;

/// <summary>
/// Branch metadata (parent, creator), the repository policy (protected branches, merge rules) and
/// human approvals for merges. Identity is self-declared until auth exists (docs/OPEN_QUESTIONS.md #4),
/// so these rules keep honest clients and agents in their lane; they are not a security boundary yet.
/// </summary>
public sealed partial class RhinoTrunkService
{
    private const string PolicyKey = "policy";
    private const string BranchKeyPrefix = "branch/";
    private const string ApprovalKeyPrefix = "approval/";

    private static async Task<T?> ReadMetaAsync<T>(RepoContext ctx, string key, CancellationToken ct) where T : class
    {
        var bytes = await ctx.Store.Meta.GetAsync(key, ct);
        return bytes is null ? null : JsonSerializer.Deserialize<T>(bytes, RhinoTrunkJson.Options);
    }

    private static Task WriteMetaAsync<T>(RepoContext ctx, string key, T? value, CancellationToken ct) where T : class =>
        ctx.Store.Meta.SetAsync(key, value is null ? null : JsonSerializer.SerializeToUtf8Bytes(value, RhinoTrunkJson.Options), ct);

    private static RhinoTrunkException Forbidden(string code, string message, Dictionary<string, string>? data = null) =>
        new(403, code, message, null, data);

    // =========================================================================================
    // Policy
    // =========================================================================================

    private static async Task<RepoPolicy> LoadPolicyAsync(RepoContext ctx, CancellationToken ct) =>
        await ReadMetaAsync<RepoPolicy>(ctx, PolicyKey, ct) ?? new RepoPolicy();

    private static async Task<bool> IsProtectedAsync(RepoContext ctx, string branch, CancellationToken ct) =>
        (await LoadPolicyAsync(ctx, ct)).ProtectedBranches.Contains(branch, StringComparer.Ordinal);

    public async Task<RepoPolicy> GetPolicyAsync(string repo, CancellationToken ct = default) =>
        await LoadPolicyAsync(await RepoAsync(repo, ct), ct);

    public async Task<RepoPolicy> SetPolicyAsync(string repo, PolicyUpdateRequest request, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var errors = new List<ApiErrorDetail>();
        CheckAuthor(request.Author, (code, message, _) => errors.Add(new ApiErrorDetail { Code = code, Message = message }));
        foreach (var b in request.ProtectedBranches ?? [])
            if (!Names.IsValidRefName(b)) errors.Add(new ApiErrorDetail { Code = ErrorCodes.InvalidRequest, Message = $"Invalid branch name '{b}'." });
        errors.AddRange(CheckRules(request.MergeRules));
        if (errors.Count > 0) throw RhinoTrunkException.BadRequest("The policy is malformed.", errors);
        if (request.Author.Kind != AuthorKinds.Human)
            throw Forbidden(ErrorCodes.Forbidden, "Only a human can change the repository policy.");

        var policy = new RepoPolicy
        {
            ProtectedBranches = (request.ProtectedBranches ?? []).Distinct(StringComparer.Ordinal).ToList(),
            MergeRules = request.MergeRules ?? [],
        };
        await ctx.WriteLock.WaitAsync(ct);
        try { await WriteMetaAsync(ctx, PolicyKey, policy, ct); }
        finally { ctx.WriteLock.Release(); }
        return policy;
    }

    private static List<ApiErrorDetail> CheckRules(IEnumerable<MergeRule>? rules)
    {
        var errors = new List<ApiErrorDetail>();
        foreach (var r in rules ?? [])
        {
            void Add(string m) => errors.Add(new ApiErrorDetail { Code = ErrorCodes.InvalidRequest, Message = $"Merge rule '{r.Name}': {m}" });
            if (string.IsNullOrWhiteSpace(r.Name)) Add("a name is required.");
            if (r.Take is not (MergeTakes.Ours or MergeTakes.Theirs)) Add("'take' must be 'ours' or 'theirs'.");
            if (r.Kind is not null && !EntityKinds.IsKnown(r.Kind)) Add($"unknown kind '{r.Kind}'.");
            foreach (var f in r.Fields ?? [])
                if (f is not ("subtype" or "refs") && !f.StartsWith("components.", StringComparison.Ordinal))
                    Add($"unknown field '{f}' (use subtype, refs or components.<name>).");
        }
        return errors;
    }

    /// <summary>Repository rules first (unless disabled), then the request's own rules.</summary>
    private static async Task<List<MergeRule>> RulesForAsync(RepoContext ctx, bool? useRepoRules, List<MergeRule>? extra, CancellationToken ct)
    {
        var rules = new List<MergeRule>();
        if (useRepoRules ?? true) rules.AddRange((await LoadPolicyAsync(ctx, ct)).MergeRules);
        rules.AddRange(extra ?? []);
        return rules;
    }

    // =========================================================================================
    // Branch metadata
    // =========================================================================================

    private static Task<BranchInfo?> LoadBranchMetaAsync(RepoContext ctx, string branch, CancellationToken ct) =>
        ReadMetaAsync<BranchInfo>(ctx, BranchKeyPrefix + branch, ct);

    private Task RecordBranchAsync(RepoContext ctx, string branch, string? parent, string? baseCommit, AuthorInfo? author, CancellationToken ct) =>
        WriteMetaAsync(ctx, BranchKeyPrefix + branch, new BranchInfo
        {
            Name = branch, Parent = string.IsNullOrEmpty(parent) ? null : parent, Base = baseCommit,
            CreatedBy = author, Created = Now(),
        }, ct);

    private static async Task<BranchInfo> ToBranchInfoAsync(RepoContext ctx, RepoPolicy policy, string name, string commit, CancellationToken ct)
    {
        var meta = await LoadBranchMetaAsync(ctx, name, ct);
        return new BranchInfo
        {
            Name = name, Commit = commit, Parent = meta?.Parent, Base = meta?.Base, CreatedBy = meta?.CreatedBy,
            Created = meta?.Created, Protected = policy.ProtectedBranches.Contains(name, StringComparer.Ordinal),
        };
    }

    public async Task<BranchesResponse> ListBranchesAsync(string repo, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var policy = await LoadPolicyAsync(ctx, ct);
        var result = new List<BranchInfo>();
        foreach (var (full, commit) in (await ctx.Store.Refs.ListAsync(ct)).OrderBy(r => r.Key, StringComparer.Ordinal))
            if (full.StartsWith(HeadsPrefix, StringComparison.Ordinal))
                result.Add(await ToBranchInfoAsync(ctx, policy, full[HeadsPrefix.Length..], commit, ct));
        return new BranchesResponse { Branches = result };
    }

    public async Task<BranchInfo> GetBranchInfoAsync(string repo, string name, CancellationToken ct = default)
    {
        CheckRef(RefTypes.Branch, name);
        var ctx = await RepoAsync(repo, ct);
        var commit = await ctx.Store.Refs.GetAsync(HeadsPrefix + name, ct) ?? throw RhinoTrunkException.NotFound($"branch '{name}' not found.");
        return await ToBranchInfoAsync(ctx, await LoadPolicyAsync(ctx, ct), name, commit, ct);
    }

    /// <summary>Creates a branch at any revision and records who created it and which branch it came from.</summary>
    public async Task<BranchInfo> CreateBranchAsync(string repo, CreateBranchRequest request, CancellationToken ct = default)
    {
        CheckRef(RefTypes.Branch, request.Name);
        var errors = new List<ApiErrorDetail>();
        CheckAuthor(request.Author, (code, message, _) => errors.Add(new ApiErrorDetail { Code = code, Message = message }));
        if (request.Parent is not null && !Names.IsValidRefName(request.Parent))
            errors.Add(new ApiErrorDetail { Code = ErrorCodes.InvalidRequest, Message = $"Invalid parent branch '{request.Parent}'." });
        if (errors.Count > 0) throw RhinoTrunkException.BadRequest("The branch request is malformed.", errors);

        var ctx = await RepoAsync(repo, ct);
        var commit = await ResolveAsync(ctx, request.From, ct);
        var parent = request.Parent;
        if (parent is null && await ctx.Store.Refs.GetAsync(HeadsPrefix + request.From, ct) is not null) parent = request.From;
        await UpdateRefAsync(repo, RefTypes.Branch, request.Name,
            new RefUpdateRequest { Commit = commit, Expected = null, Author = request.Author, Parent = parent }, request.Author.Id, ct);
        return await GetBranchInfoAsync(repo, request.Name, ct);
    }

    // =========================================================================================
    // Approvals
    // =========================================================================================

    public async Task<ApprovalInfo> CreateApprovalAsync(string repo, ApprovalRequest request, CancellationToken ct = default)
    {
        var errors = new List<ApiErrorDetail>();
        CheckAuthor(request.By, (code, message, _) => errors.Add(new ApiErrorDetail { Code = code, Message = "by: " + message }));
        if (!Names.IsValidRefName(request.Branch)) errors.Add(new ApiErrorDetail { Code = ErrorCodes.InvalidRequest, Message = $"Invalid branch name '{request.Branch}'." });
        if (string.IsNullOrWhiteSpace(request.Theirs)) errors.Add(new ApiErrorDetail { Code = ErrorCodes.InvalidRequest, Message = "theirs is required." });
        if (errors.Count > 0) throw RhinoTrunkException.BadRequest("The approval is malformed.", errors);
        if (request.By.Kind != AuthorKinds.Human)
            throw Forbidden(ErrorCodes.Forbidden, "Only a human can approve a merge.");

        var ctx = await RepoAsync(repo, ct);
        var ours = await ctx.Store.Refs.GetAsync(HeadsPrefix + request.Branch, ct)
                   ?? throw RhinoTrunkException.NotFound($"Branch '{request.Branch}' not found.");
        var theirs = await ResolveAsync(ctx, request.Theirs, ct);
        var approval = new ApprovalInfo
        {
            Id = Guid.NewGuid().ToString("N"), Branch = request.Branch, Ours = ours, Theirs = theirs,
            TheirsRef = Uri.UnescapeDataString(request.Theirs), By = request.By, Note = request.Note, Timestamp = Now(),
        };
        await WriteMetaAsync(ctx, ApprovalKeyPrefix + approval.Id, approval, ct);
        return approval;
    }

    public async Task<ApprovalInfo> GetApprovalAsync(string repo, string id, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var a = await ReadMetaAsync<ApprovalInfo>(ctx, ApprovalKeyPrefix + id, ct) ?? throw RhinoTrunkException.NotFound($"Approval '{id}' not found.");
        return await WithStatusAsync(ctx, a, ct);
    }

    public async Task<ApprovalsResponse> ListApprovalsAsync(string repo, string? branch, string? theirs, CancellationToken ct = default)
    {
        var ctx = await RepoAsync(repo, ct);
        var result = new List<ApprovalInfo>();
        foreach (var key in await ctx.Store.Meta.ListAsync(ApprovalKeyPrefix, ct))
        {
            var a = await ReadMetaAsync<ApprovalInfo>(ctx, key, ct);
            if (a is null || (branch is not null && a.Branch != branch)) continue;
            if (theirs is not null && a.TheirsRef != theirs && a.Theirs != theirs) continue;
            result.Add(await WithStatusAsync(ctx, a, ct));
        }
        return new ApprovalsResponse { Approvals = result.OrderByDescending(a => a.Timestamp).ToList() };
    }

    private static async Task<ApprovalInfo> WithStatusAsync(RepoContext ctx, ApprovalInfo a, CancellationToken ct)
    {
        if (a.UsedBy is not null) return a with { Status = ApprovalStatuses.Used };
        var ours = await ctx.Store.Refs.GetAsync(HeadsPrefix + a.Branch, ct);
        string? theirs;
        try { theirs = await ResolveAsync(ctx, a.TheirsRef, ct); }
        catch (RhinoTrunkException) { theirs = null; }
        return a with { Status = ours == a.Ours && theirs == a.Theirs ? ApprovalStatuses.Active : ApprovalStatuses.Stale };
    }

    /// <summary>Why a merge of <paramref name="author"/> into <paramref name="branch"/> needs a human approval (empty: it does not).</summary>
    private static async Task<List<string>> ApprovalReasonsAsync(RepoContext ctx, string? branch, AuthorInfo? author, bool rulesApplied, CancellationToken ct)
    {
        var reasons = new List<string>();
        var isProtected = branch is not null && await IsProtectedAsync(ctx, branch, ct);
        if (isProtected) reasons.Add(ApprovalReasons.ProtectedBranch);
        if (rulesApplied) reasons.Add(ApprovalReasons.RulesApplied);
        if (branch is not null && !isProtected && author?.Kind == AuthorKinds.Agent)
        {
            var meta = await LoadBranchMetaAsync(ctx, branch, ct);
            if (meta?.CreatedBy?.Id != author.Id) reasons.Add(ApprovalReasons.NotBranchOwner);
        }
        return reasons;
    }

    /// <summary>
    /// Enforces the approval rules for a merge. A human author approves their own merge, except that conflicts
    /// resolved by rules need an explicit confirmation. An agent always needs a stored human approval for
    /// exactly these heads. Returns the approver (null when no approval was needed).
    /// </summary>
    private static async Task<(AuthorInfo? By, ApprovalInfo? Approval)> CheckApprovalAsync(
        RepoContext ctx, MergeRequest request, string ours, string theirs, List<string> reasons, int autoResolved, CancellationToken ct)
    {
        ApprovalInfo? approval = null;
        if (request.Approval is not null)
        {
            approval = await ReadMetaAsync<ApprovalInfo>(ctx, ApprovalKeyPrefix + request.Approval, ct)
                       ?? throw RhinoTrunkException.NotFound($"Approval '{request.Approval}' not found.");
            if (approval.UsedBy is not null)
                throw RhinoTrunkException.Conflict(ErrorCodes.ApprovalStale, $"Approval {approval.Id} was already used (by {approval.UsedBy}).");
            if (approval.Branch != request.Branch || approval.Ours != ours || approval.Theirs != theirs)
                throw RhinoTrunkException.Conflict(ErrorCodes.ApprovalStale,
                    $"Approval {approval.Id} was given for {approval.Theirs} into {approval.Branch} at {approval.Ours}; " +
                    $"now {theirs} into {request.Branch} at {ours}. Ask for a new approval.",
                    new Dictionary<string, string> { ["ours"] = ours, ["theirs"] = theirs });
        }
        if (reasons.Count == 0) return (approval?.By, approval);
        if (approval is not null) return (approval.By, approval);

        var data = new Dictionary<string, string> { ["reasons"] = string.Join(",", reasons), ["ours"] = ours, ["theirs"] = theirs };
        if (request.Author.Kind == AuthorKinds.Human)
        {
            if (reasons.Contains(ApprovalReasons.RulesApplied) && !request.ApproveRuleResolutions)
                throw Forbidden(ErrorCodes.ApprovalRequired,
                    $"Merge rules resolved {autoResolved} conflicting entities. Review them (POST merges/preview) and confirm with approveRuleResolutions: true.", data);
            return (request.Author, null);
        }
        throw Forbidden(ErrorCodes.ApprovalRequired,
            $"An agent cannot merge into '{request.Branch}' without a human approval ({string.Join(", ", reasons)}). " +
            "Ask a human to approve (POST /approvals) and pass its id as 'approval'.", data);
    }

    private static async Task MarkApprovalUsedAsync(RepoContext ctx, ApprovalInfo? approval, string commit, CancellationToken ct)
    {
        if (approval is null) return;
        await WriteMetaAsync(ctx, ApprovalKeyPrefix + approval.Id, approval with { UsedBy = commit, Status = ApprovalStatuses.Used }, ct);
    }
}
