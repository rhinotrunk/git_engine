using System.Text.Json.Nodes;
using RhinoTrunk.Client;
using RhinoTrunk.Contracts;
using RhinoTrunk.Core;

namespace RhinoTrunk.Tests;

/// <summary>Branch metadata, protected branches, approvals, merge rules and merge previews.</summary>
public sealed class PolicyAndPreviewTests
{
    private static readonly AuthorInfo Bot = TestData.Bot;
    private static readonly AuthorInfo OtherBot = new() { Id = "agent:other", Kind = AuthorKinds.Agent };

    private static async Task<string> AgentBranchAsync(Scenario s, string name, AuthorInfo author, string from = "main") =>
        (await s.Service.CreateBranchAsync(s.Repo, new CreateBranchRequest { Name = name, From = from, Author = author })).Commit;

    private static CommitRequest AgentCommit(string branch, string? expected, params Change[] changes) => new()
    {
        Branch = branch, ExpectedHead = expected, Message = "agent work", Author = Bot, Changes = changes.ToList(),
    };

    [Test]
    public async Task BranchesRecordParentAndCreator()
    {
        var s = await Scenario.CreateAsync();
        await AgentBranchAsync(s, "agent/study", Bot);
        await AgentBranchAsync(s, "agent/study-b", Bot, from: "agent/study");
        var info = await s.Service.GetBranchInfoAsync(s.Repo, "agent/study-b");
        Assert.Equal("agent/study", info.Parent);
        Assert.Equal(Bot.Id, info.CreatedBy!.Id);
        Assert.Equal(s.Head, info.Base);
        Assert.False(info.Protected);

        var all = await s.Service.ListBranchesAsync(s.Repo);
        Assert.True(all.Branches.Single(b => b.Name == "main").Protected, "main is protected by default");
        Assert.Equal(TestData.Alex.Id, all.Branches.Single(b => b.Name == "main").CreatedBy!.Id, "first commit records the creator");

        await s.Service.DeleteRefAsync(s.Repo, RefTypes.Branch, "agent/study-b", s.Head);
        var e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.GetBranchInfoAsync(s.Repo, "agent/study-b"));
        Assert.Equal(404, e.Status);
    }

    [Test]
    public async Task AgentsCannotCommitToOrMoveProtectedBranches()
    {
        var s = await Scenario.CreateAsync();
        var e = await Assert.ThrowsAsync<RhinoTrunkException>(() =>
            s.Service.CommitAsync(s.Repo, AgentCommit("main", s.Head, TestData.Layer(Changes.NewId(), "Agent layer"))));
        Assert.Equal(403, e.Status);
        Assert.Equal(ErrorCodes.Forbidden, e.Code);

        var start = await AgentBranchAsync(s, "agent/study", Bot);
        var c = await s.Service.CommitAsync(s.Repo, AgentCommit("agent/study", start, TestData.Layer(Changes.NewId(), "Agent layer")));
        e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.UpdateRefAsync(s.Repo, RefTypes.Branch, "main",
            new RefUpdateRequest { Commit = c.Commit, Expected = s.Head, Author = Bot }));
        Assert.Equal(ErrorCodes.Forbidden, e.Code);
        e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.DeleteRefAsync(s.Repo, RefTypes.Branch, "main", s.Head));
        Assert.Equal(ErrorCodes.Forbidden, e.Code);
    }

    [Test]
    public async Task AgentMergeIntoMainNeedsAFreshHumanApproval()
    {
        var s = await Scenario.CreateAsync();
        var start = await AgentBranchAsync(s, "agent/study", Bot);
        var c = await s.Service.CommitAsync(s.Repo, AgentCommit("agent/study", start, TestData.Layer(Changes.NewId(), "Study")));
        var merge = new MergeRequest { Branch = "main", Theirs = "agent/study", Author = Bot };

        var e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.MergeAsync(s.Repo, merge));
        Assert.Equal(403, e.Status);
        Assert.Equal(ErrorCodes.ApprovalRequired, e.Code);
        Assert.Equal(ApprovalReasons.ProtectedBranch, e.Details!["reasons"]);

        e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.CreateApprovalAsync(s.Repo,
            new ApprovalRequest { Branch = "main", Theirs = "agent/study", By = Bot }));
        Assert.Equal(ErrorCodes.Forbidden, e.Code);

        var approval = await s.Service.CreateApprovalAsync(s.Repo, new ApprovalRequest { Branch = "main", Theirs = "agent/study", By = TestData.Alex });
        Assert.Equal(ApprovalStatuses.Active, (await s.Service.GetApprovalAsync(s.Repo, approval.Id)).Status);

        var result = await s.Service.MergeAsync(s.Repo, merge with { Approval = approval.Id });
        Assert.Equal(MergeStatuses.FastForward, result.Status);
        Assert.Equal(c.Commit, result.Commit);
        Assert.Equal(TestData.Alex.Id, result.ApprovedBy!.Id);
        Assert.Equal(ApprovalStatuses.Used, (await s.Service.GetApprovalAsync(s.Repo, approval.Id)).Status);

        // used approvals cannot be replayed, and an approval goes stale when a head moves
        var c2 = await s.Service.CommitAsync(s.Repo, AgentCommit("agent/study", c.Commit, TestData.Layer(Changes.NewId(), "Study 2")));
        e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.MergeAsync(s.Repo, merge with { Approval = approval.Id }));
        Assert.Equal(ErrorCodes.ApprovalStale, e.Code);
        var second = await s.Service.CreateApprovalAsync(s.Repo, new ApprovalRequest { Branch = "main", Theirs = "agent/study", By = TestData.Alex });
        await s.Service.CommitAsync(s.Repo, AgentCommit("agent/study", c2.Commit, TestData.Layer(Changes.NewId(), "Study 3")));
        Assert.Equal(ApprovalStatuses.Stale, (await s.Service.GetApprovalAsync(s.Repo, second.Id)).Status);
        e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.MergeAsync(s.Repo, merge with { Approval = second.Id }));
        Assert.Equal(ErrorCodes.ApprovalStale, e.Code);
    }

    [Test]
    public async Task AgentsMergeFreelyOnlyIntoBranchesTheyCreated()
    {
        var s = await Scenario.CreateAsync();
        var start = await AgentBranchAsync(s, "agent/study", Bot);
        await AgentBranchAsync(s, "agent/study-sub", Bot, from: "agent/study");
        var sub = await s.Service.CommitAsync(s.Repo, AgentCommit("agent/study-sub", start, TestData.Layer(Changes.NewId(), "Sub")));
        var own = await s.Service.MergeAsync(s.Repo, new MergeRequest { Branch = "agent/study", Theirs = "agent/study-sub", Author = Bot });
        Assert.Equal(MergeStatuses.FastForward, own.Status);
        Assert.Null(own.ApprovedBy);

        await s.Service.CommitAsync(s.Repo, AgentCommit("agent/study-sub", sub.Commit, TestData.Layer(Changes.NewId(), "Sub 2")));
        var e = await Assert.ThrowsAsync<RhinoTrunkException>(() =>
            s.Service.MergeAsync(s.Repo, new MergeRequest { Branch = "agent/study", Theirs = "agent/study-sub", Author = OtherBot }));
        Assert.Equal(ErrorCodes.ApprovalRequired, e.Code);
        Assert.Equal(ApprovalReasons.NotBranchOwner, e.Details!["reasons"]);

        // a human may merge into any unprotected branch without a separate approval
        var human = await s.Service.MergeAsync(s.Repo, new MergeRequest { Branch = "agent/study", Theirs = "agent/study-sub", Author = TestData.Alex });
        Assert.Equal(MergeStatuses.FastForward, human.Status);
    }

    [Test]
    public async Task PreviewShowsConflictsWithAllVersionsAndMovesNothing()
    {
        var s = await Scenario.CreateAsync();
        var start = await AgentBranchAsync(s, "agent/study", Bot);
        var added = Changes.NewId();
        var agentGeometry = await s.UploadAsync(TestData.Geometry("agent box"));
        await s.Service.CommitAsync(s.Repo, AgentCommit("agent/study", start,
            TestData.Object(s.Box, s.Walls, agentGeometry, "Agent name"),
            TestData.Object(added, s.Walls, agentGeometry, "New")));
        await s.CommitAsync("human renames", TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Human name"));
        var mainBefore = s.Head;

        var preview = await s.Service.PreviewMergeAsync(s.Repo, new MergePreviewRequest
        {
            Branch = "main", Theirs = "agent/study", Author = Bot, CompareTo = start, Manifests = true,
        });
        Assert.Equal(MergePreviewStatuses.Conflicts, preview.Status);
        Assert.False(preview.CanMerge);
        var conflict = Assert.Single(preview.Conflicts);
        Assert.Equal(s.Box, conflict.Id);
        Assert.SequenceEqual(["components.attributes"], conflict.Fields!);
        Assert.True(conflict.Base is not null && conflict.Ours is not null && conflict.Theirs is not null);
        Assert.SequenceEqual([ApprovalReasons.ProtectedBranch], preview.ApprovalReasons);
        Assert.Equal(1, preview.Incoming.Added);
        var newObject = preview.Changes.Single(c => c.Id == added);
        Assert.Equal(DiffChanges.Added, newObject.Change);
        Assert.Equal(agentGeometry, newObject.ToManifest!.Components["geometry"]);
        Assert.Equal(mainBefore, (await s.Service.GetRefAsync(s.Repo, RefTypes.Branch, "main")).Commit, "preview moves nothing");

        // the same preview into an older point on main has no conflict
        var atStart = await s.Service.PreviewMergeAsync(s.Repo, new MergePreviewRequest { Ours = start, Theirs = "agent/study" });
        Assert.Equal(MergePreviewStatuses.FastForward, atStart.Status);
        Assert.True(atStart.CanMerge);
    }

    [Test]
    public async Task RulesResolveConflictsButNeedExplicitApproval()
    {
        var s = await Scenario.CreateAsync();
        var start = await AgentBranchAsync(s, "agent/study", Bot);
        await s.Service.CommitAsync(s.Repo, AgentCommit("agent/study", start, TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Agent name")));
        await s.CommitAsync("human renames", TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Human name"));

        var e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.SetPolicyAsync(s.Repo, new PolicyUpdateRequest { Author = Bot }));
        Assert.Equal(ErrorCodes.Forbidden, e.Code);
        await s.Service.SetPolicyAsync(s.Repo, new PolicyUpdateRequest
        {
            ProtectedBranches = ["main"], Author = TestData.Alex,
            MergeRules = [new MergeRule { Name = "agent names win", Kind = EntityKinds.Object, Fields = ["components.attributes"], Take = MergeTakes.Theirs }],
        });

        var preview = await s.Service.PreviewMergeAsync(s.Repo, new MergePreviewRequest { Branch = "main", Theirs = "agent/study", Author = TestData.Alex });
        Assert.Equal(MergePreviewStatuses.Clean, preview.Status);
        Assert.True(preview.CanMerge);
        Assert.Equal("agent names win", Assert.Single(preview.AutoResolved).Rule);
        Assert.SequenceEqual([ApprovalReasons.ProtectedBranch, ApprovalReasons.RulesApplied], preview.ApprovalReasons);

        var merge = new MergeRequest { Branch = "main", Theirs = "agent/study", Author = TestData.Alex };
        e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.MergeAsync(s.Repo, merge));
        Assert.Equal(ErrorCodes.ApprovalRequired, e.Code);
        var result = await s.Service.MergeAsync(s.Repo, merge with { ApproveRuleResolutions = true });
        Assert.Equal(MergeStatuses.Merged, result.Status);
        Assert.Equal("agent names win", Assert.Single(result.AutoResolved!).Rule);
        var commit = await s.Service.GetCommitAsync(s.Repo, "main");
        Assert.Equal("agent names win", commit.Meta!["merge.rules"]);
        var box = await s.Service.GetEntityAsync(s.Repo, "main", "main", EntityKinds.Object, s.Box);
        var agentAttrs = (await s.Service.GetEntityAsync(s.Repo, "agent/study", "main", EntityKinds.Object, s.Box)).Manifest!.Components["attributes"];
        Assert.Equal(agentAttrs, box.Manifest!.Components["attributes"]);
    }

    [Test]
    public async Task WholeEntityRulesResolveModifyDeleteConflicts()
    {
        var s = await Scenario.CreateAsync();
        var start = await AgentBranchAsync(s, "agent/study", Bot);
        await s.Service.CommitAsync(s.Repo, AgentCommit("agent/study", start, Changes.Delete(EntityKinds.Object, s.Box)));
        await s.CommitAsync("human renames", TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Human name"));
        var preview = await s.Service.PreviewMergeAsync(s.Repo, new MergePreviewRequest
        {
            Branch = "main", Theirs = "agent/study",
            Rules = [new MergeRule { Name = "keep ours", Take = MergeTakes.Ours }],
        });
        Assert.Equal(MergePreviewStatuses.Clean, preview.Status);
        Assert.Null(Assert.Single(preview.AutoResolved).Fields);
        Assert.Equal(0, preview.Changes.Count, "keeping ours changes nothing on main");
    }

    [Test]
    public async Task DiffCanIncludeManifestsAndMergeBaseIsExposed()
    {
        var s = await Scenario.CreateAsync();
        var first = s.Head;
        var start = await AgentBranchAsync(s, "agent/study", Bot);
        await s.Service.CommitAsync(s.Repo, AgentCommit("agent/study", start, TestData.Layer(Changes.NewId(), "Study")));
        await s.CommitAsync("human renames", TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Renamed"));
        var mb = await s.Service.MergeBaseAsync(s.Repo, "main", "agent/study");
        Assert.Equal(first, mb.MergeBase);

        var diff = await s.Service.DiffAsync(s.Repo, first, "main", includeManifests: true);
        var entry = Assert.Single(diff.Changes);
        Assert.NotNull(entry.FromManifest);
        Assert.Equal(EntityKinds.Object, entry.ToManifest!.Kind);
        Assert.Null((await s.Service.DiffAsync(s.Repo, first, "main")).Changes[0].ToManifest);
    }

    [Test]
    public async Task ApiExposesBranchesPreviewAndApprovals()
    {
        await using var server = await ApiServer.StartAsync(fileSystem: true);
        var api = server.Client;
        const string repo = "policy-api";
        await api.CreateRepoAsync(repo);
        var layer = Changes.NewId();
        var first = await api.CommitAsync(repo, new CommitRequest
        {
            Branch = "main", Message = "start", Author = TestData.Alex,
            Changes = [TestData.Settings(), TestData.Layer(layer, "Default")],
        });
        var branch = await api.CreateBranchAsync(repo, new CreateBranchRequest { Name = "agent/x", From = "main", Author = Bot });
        Assert.Equal("main", branch.Parent);
        await api.CommitAsync(repo, AgentCommit("agent/x", first.Commit, TestData.Layer(Changes.NewId(), "X")));

        var preview = await api.PreviewMergeAsync(repo, new MergePreviewRequest { Branch = "main", Theirs = "agent/x", Author = Bot, Manifests = true });
        Assert.Equal(MergePreviewStatuses.FastForward, preview.Status);
        Assert.True(preview.RequiresApproval);
        Assert.NotNull(Assert.Single(preview.Changes).ToManifest);

        var denied = await Assert.ThrowsAsync<RhinoTrunkApiException>(() =>
            api.MergeAsync(repo, new MergeRequest { Branch = "main", Theirs = "agent/x", Author = Bot }));
        Assert.Equal(403, (int)denied.StatusCode);
        Assert.Equal(ErrorCodes.ApprovalRequired, denied.Error.Code);

        var approval = await api.CreateApprovalAsync(repo, new ApprovalRequest { Branch = "main", Theirs = "agent/x", By = TestData.Alex, Note = "looks good" });
        Assert.Single((await api.ListApprovalsAsync(repo, branch: "main")).Approvals);
        var merged = await api.MergeAsync(repo, new MergeRequest { Branch = "main", Theirs = "agent/x", Author = Bot, Approval = approval.Id });
        Assert.Equal(MergeStatuses.FastForward, merged.Status);
        Assert.Equal(ApprovalStatuses.Used, Assert.Single((await api.ListApprovalsAsync(repo)).Approvals).Status);
        Assert.True((await api.GetPolicyAsync(repo)).ProtectedBranches.Contains("main"));
        Assert.Equal(2, (await api.ListBranchesAsync(repo)).Branches.Count);
    }
}
