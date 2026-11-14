using System.Text.Json.Nodes;
using RhinoTrunk.Client;
using RhinoTrunk.Contracts;
using RhinoTrunk.Core;
using RhinoTrunk.Core.Storage;

namespace RhinoTrunk.Tests;

public sealed class MergeTests
{
    private static MergeRequest Merge(string theirs, List<MergeResolution>? resolutions = null) =>
        new() { Branch = "main", Theirs = theirs, Author = TestData.Alex, Resolutions = resolutions };

    private static async Task<Manifest> ManifestAt(Scenario s, string rev, string kind, string id) =>
        (await s.Service.GetEntityAsync(s.Repo, rev, "main", kind, id)).Manifest!;

    [Test]
    public async Task FastForwardsWhenMainHasNotMoved()
    {
        var s = await Scenario.CreateAsync();
        var start = await s.BranchAsync("agent/study");
        var c = await s.CommitOnAsync("agent/study", start, "agent work", TestData.Layer(Changes.NewId(), "Study"));
        var result = await s.Service.MergeAsync(s.Repo, Merge("agent/study"));
        Assert.Equal(MergeStatuses.FastForward, result.Status);
        Assert.Equal(c.Commit, (await s.Service.GetRefAsync(s.Repo, RefTypes.Branch, "main")).Commit);

        var again = await s.Service.MergeAsync(s.Repo, Merge("agent/study"));
        Assert.Equal(MergeStatuses.UpToDate, again.Status);
    }

    [Test]
    public async Task DisjointChangesMergeCleanly()
    {
        var s = await Scenario.CreateAsync();
        var start = await s.BranchAsync("agent/study");
        var studyLayer = Changes.NewId();
        await s.CommitOnAsync("agent/study", start, "agent adds a layer", TestData.Layer(studyLayer, "Study"));
        var roof = Changes.NewId();
        await s.CommitAsync("human adds a roof", TestData.Object(roof, s.Layer0, s.BoxGeometry, "Roof"));

        var result = await s.Service.MergeAsync(s.Repo, Merge("agent/study"));
        Assert.Equal(MergeStatuses.Merged, result.Status);
        Assert.Equal(start, result.MergeBase);
        var commit = await s.Service.GetCommitAsync(s.Repo, "main");
        Assert.Equal(2, commit.Parents.Count);
        Assert.Equal(3, commit.Generation);
        var ids = (await s.Service.ListEntitiesAsync(s.Repo, "main", null, null, false)).Entities.Select(e => e.Id).ToHashSet();
        Assert.True(ids.Contains(studyLayer) && ids.Contains(roof));
    }

    [Test]
    public async Task EditsToDifferentComponentsOfOneObjectCombine()
    {
        var s = await Scenario.CreateAsync();
        var start = await s.BranchAsync("agent/study");
        var newGeometry = await s.UploadAsync(TestData.Geometry("box moved"));
        await s.CommitOnAsync("agent/study", start, "agent moves the box", TestData.Object(s.Box, s.Walls, newGeometry, "Box"));
        await s.CommitAsync("human renames the box", TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Box (renamed)"));
        var renamedAttributes = (await ManifestAt(s, "main", EntityKinds.Object, s.Box)).Components["attributes"];

        var result = await s.Service.MergeAsync(s.Repo, Merge("agent/study"));
        Assert.Equal(MergeStatuses.Merged, result.Status);
        var merged = await ManifestAt(s, "main", EntityKinds.Object, s.Box);
        Assert.Equal(newGeometry, merged.Components["geometry"], "geometry from the agent");
        Assert.Equal(renamedAttributes, merged.Components["attributes"], "attributes from the human");
    }

    [Test]
    public async Task ConflictingEditsAreReportedAndCanBeResolved()
    {
        var s = await Scenario.CreateAsync();
        var start = await s.BranchAsync("agent/study");
        await s.CommitOnAsync("agent/study", start, "agent renames", TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Agent name"));
        await s.CommitAsync("human renames", TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Human name"));

        var e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.MergeAsync(s.Repo, Merge("agent/study")));
        Assert.Equal(ErrorCodes.MergeConflict, e.Code);
        var conflict = Assert.Single(e.Errors!);
        Assert.Equal(s.Box, conflict.Id);
        Assert.SequenceEqual(["components.attributes"], conflict.Fields!);

        var resolved = await s.Service.MergeAsync(s.Repo, Merge("agent/study",
            [new MergeResolution { Kind = EntityKinds.Object, Id = s.Box, Take = MergeTakes.Theirs }]));
        Assert.Equal(MergeStatuses.Merged, resolved.Status);
        var agentVersion = (await s.Service.GetEntityAsync(s.Repo, "agent/study", "main", EntityKinds.Object, s.Box)).Version;
        Assert.Equal(agentVersion, (await s.Service.GetEntityAsync(s.Repo, "main", "main", EntityKinds.Object, s.Box)).Version);
    }

    [Test]
    public async Task SemanticConflictsAreCaughtByValidation()
    {
        var s = await Scenario.CreateAsync();
        var start = await s.BranchAsync("agent/study");
        await s.CommitOnAsync("agent/study", start, "agent adds a panel on Walls", TestData.Object(Changes.NewId(), s.Walls, s.BoxGeometry, "Panel"));
        await s.CommitAsync("human removes Walls", Changes.Delete(EntityKinds.Layer, s.Walls), Changes.Delete(EntityKinds.Object, s.Box));

        var e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.MergeAsync(s.Repo, Merge("agent/study")));
        Assert.Equal(ErrorCodes.ValidationFailed, e.Code);
        Assert.Contains(e.Errors!, d => d.Code == ErrorCodes.MissingRef);
    }

    [Test]
    public async Task SameLayerNameAddedOnBothSidesIsCaught()
    {
        var s = await Scenario.CreateAsync();
        var start = await s.BranchAsync("agent/study");
        await s.CommitOnAsync("agent/study", start, "agent adds Glass", TestData.Layer(Changes.NewId(), "Glass"));
        await s.CommitAsync("human adds Glass", TestData.Layer(Changes.NewId(), "Glass"));

        var e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.MergeAsync(s.Repo, Merge("agent/study")));
        Assert.Equal(ErrorCodes.ValidationFailed, e.Code);
        Assert.Contains(e.Errors!, d => d.Code == ErrorCodes.DuplicateLayerName);
    }

    [Test]
    public void ManifestMergeIsFieldByField()
    {
        Manifest M(string geometry, string attributes, string? subtype = "Brep") =>
            new() { Kind = "object", Subtype = subtype, Components = new() { ["geometry"] = geometry, ["attributes"] = attributes } };
        var (merged, conflicts) = RhinoTrunkService.MergeManifests(M("g0", "a0"), M("g1", "a0", "Mesh"), M("g0", "a1"));
        Assert.Equal(0, conflicts.Count);
        Assert.Equal("g1", merged!.Components["geometry"]);
        Assert.Equal("a1", merged.Components["attributes"]);
        Assert.Equal("Mesh", merged.Subtype);

        (merged, conflicts) = RhinoTrunkService.MergeManifests(M("g0", "a0"), M("g1", "a0"), M("g2", "a0"));
        Assert.Null(merged);
        Assert.SequenceEqual(["components.geometry"], conflicts);
    }
}

public sealed class GraphAndRefTests
{
    [Test]
    public async Task GraphListsChildrenBeforeParentsWithLabels()
    {
        var s = await Scenario.CreateAsync();
        var root = s.Head;
        var start = await s.BranchAsync("agent/study");
        var agent = await s.CommitOnAsync("agent/study", start, "agent", TestData.Layer(Changes.NewId(), "Study"));
        await s.CommitAsync("human", TestData.Layer(Changes.NewId(), "Roof"));
        var merge = await s.Service.MergeAsync(s.Repo, new MergeRequest { Branch = "main", Theirs = "agent/study", Author = TestData.Alex });
        await s.Service.UpdateRefAsync(s.Repo, RefTypes.Tag, "v1", new RefUpdateRequest { Commit = merge.Commit });

        var graph = await s.Service.GraphAsync(s.Repo, null, 100);
        Assert.Equal(4, graph.Commits.Count);
        Assert.False(graph.Truncated);
        Assert.Equal(merge.Commit, graph.Commits[0].Id);
        Assert.SequenceEqual(["main", "tag:v1"], graph.Commits[0].Refs);
        Assert.Equal(root, graph.Commits[^1].Id);
        var position = graph.Commits.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        foreach (var c in graph.Commits)
            foreach (var p in c.Parents)
                Assert.True(position[c.Id] < position[p], "a commit is listed before its parents");
        Assert.Contains(graph.Commits, c => c.Id == agent.Commit && c.Refs.Contains("agent/study"));

        var page = await s.Service.GraphAsync(s.Repo, null, 2);
        Assert.Equal(2, page.Commits.Count);
        Assert.True(page.Truncated);
    }

    [Test]
    public async Task HistoryFollowsOneEntity()
    {
        var s = await Scenario.CreateAsync();
        await s.CommitAsync("unrelated", TestData.Layer(Changes.NewId(), "Other"));
        await s.CommitAsync("rename box", TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Renamed"));
        await s.CommitAsync("delete box", Changes.Delete(EntityKinds.Object, s.Box));

        var history = await s.Service.HistoryAsync(s.Repo, "main", "main", EntityKinds.Object, s.Box, 100);
        Assert.SequenceEqual([DiffChanges.Deleted, DiffChanges.Modified, DiffChanges.Added], history.Entries.Select(e => e.Change));
        Assert.SequenceEqual(["delete box", "rename box", "Initial document"], history.Entries.Select(e => e.Message));
    }

    [Test]
    public async Task RefsUseCompareAndSwapAndTagsAreImmutable()
    {
        var s = await Scenario.CreateAsync();
        var first = s.Head;
        await s.BranchAsync("feature");
        var e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.BranchAsync("feature"));
        Assert.Equal(ErrorCodes.RefConflict, e.Code);

        await s.CommitAsync("second", TestData.Layer(Changes.NewId(), "Second"));
        await s.Service.UpdateRefAsync(s.Repo, RefTypes.Branch, "feature", new RefUpdateRequest { Commit = s.Head, Expected = first });
        await s.Service.UpdateRefAsync(s.Repo, RefTypes.Tag, "v1", new RefUpdateRequest { Commit = first });
        e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.UpdateRefAsync(s.Repo, RefTypes.Tag, "v1", new RefUpdateRequest { Commit = s.Head, Expected = first }));
        Assert.Equal(ErrorCodes.TagImmutable, e.Code);

        await s.Service.DeleteRefAsync(s.Repo, RefTypes.Branch, "feature", s.Head);
        var refs = (await s.Service.ListRefsAsync(s.Repo)).Refs.Select(r => $"{r.Type}:{r.Name}").ToList();
        Assert.SequenceEqual(["branch:main", "tag:v1"], refs);
        Assert.Equal(first, await s.Service.ResolveAsync(s.Repo, "v1"));
    }

    [Test]
    public async Task AnnotationsAndDerivedDataAttachToContent()
    {
        var s = await Scenario.CreateAsync();
        await s.Service.AddAnnotationAsync(s.Repo, "main", new Annotation
        {
            Namespace = "ci/geometry-valid", Status = AnnotationStatuses.Success, Summary = "All 1 objects valid",
            Data = new JsonObject { ["checked"] = 1 }, Author = TestData.Bot,
        });
        var list = await s.Service.ListAnnotationsAsync(s.Repo, s.Head);
        Assert.Equal("ci/geometry-valid", Assert.Single(list.Annotations).Namespace);
        Assert.Equal(s.Head, (await s.Service.GetCommitAsync(s.Repo, "main")).Id, "annotations never change the commit");

        var preview = await s.UploadAsync("glb preview bytes"u8.ToArray());
        await s.Service.SetDerivedAsync(s.Repo, s.BoxGeometry, "preview-mesh.v1", new DerivedPutRequest { Result = preview });
        Assert.Equal(preview, (await s.Service.GetDerivedAsync(s.Repo, s.BoxGeometry, "preview-mesh.v1")).Result);
        var e = await Assert.ThrowsAsync<RhinoTrunkException>(() => s.Service.SetDerivedAsync(s.Repo, s.BoxGeometry, "bbox.v1",
            new DerivedPutRequest { Result = ContentHash.Compute("not uploaded"u8) }));
        Assert.Equal(ErrorCodes.ObjectMissing, e.Code);
    }
}

public sealed class FileSystemStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rhinotrunk-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Test]
    public async Task DataSurvivesARestart()
    {
        var s = await Scenario.CreateAsync(new FileSystemRepositoryProvider(_dir));
        await s.CommitAsync("rename", TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Renamed"));

        var reopened = new RhinoTrunkService(new FileSystemRepositoryProvider(_dir));
        Assert.Equal(s.Head, (await reopened.GetCommitAsync(TestData.Repo, "main")).Id);
        Assert.Equal(4, (await reopened.ListEntitiesAsync(TestData.Repo, "main", null, null, false)).Entities.Count);
        Assert.Equal(TestData.Repo, Assert.Single((await reopened.ListReposAsync()).Repos).Name);
        Assert.True(File.Exists(Path.Combine(_dir, TestData.Repo, "refs.json")));

        var log = await (await new FileSystemRepositoryProvider(_dir).GetAsync(TestData.Repo))!.Refs.ReadLogAsync();
        Assert.Equal(2, log.Count);
        Assert.Equal("alex", log[1].Actor);
    }
}
