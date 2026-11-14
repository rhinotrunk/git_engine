using System.Text.Json.Nodes;
using RhinoTrunk.Client;
using RhinoTrunk.Contracts;
using RhinoTrunk.Core;
using RhinoTrunk.Core.Storage;

namespace RhinoTrunk.Tests;

public sealed class CommitTests
{
    private static async Task<RhinoTrunkException> Rejected(Func<Task> action, int status, string code)
    {
        var e = await Assert.ThrowsAsync<RhinoTrunkException>(action);
        Assert.Equal(status, e.Status, e.Message);
        Assert.Equal(code, e.Code, e.Message);
        return e;
    }

    private static void HasDetail(RhinoTrunkException e, string code) =>
        Assert.Contains(e.Errors ?? [], d => d.Code == code, $"expected detail '{code}', got: {string.Join("; ", (e.Errors ?? []).Select(x => x.Code + " " + x.Message))}");

    [Test]
    public async Task InitialCommitCreatesBranchAndSnapshot()
    {
        var s = await Scenario.CreateAsync();
        var commit = await s.Service.GetCommitAsync(s.Repo, "main");
        Assert.Equal(s.Head, commit.Id);
        Assert.Equal(1, commit.Generation);
        Assert.Equal(0, commit.Parents.Count);

        var entities = await s.Service.ListEntitiesAsync(s.Repo, "main", null, null, includeManifests: true);
        Assert.Equal(4, entities.Entities.Count);
        var box = Assert.Single(entities.Entities, e => e.Id == s.Box);
        Assert.Equal("Brep", box.Manifest!.Subtype);
        Assert.Equal(s.BoxGeometry, box.Manifest.Components["geometry"]);
        Assert.True(ContentHash.IsValid(box.Manifest.Components["attributes"]));
        Assert.Equal(s.Walls, Assert.Single(box.Manifest.Refs).Id);
    }

    [Test]
    public async Task FirstCommitMustContainSettingsAndALayer()
    {
        var svc = new RhinoTrunkService(new InMemoryRepositoryProvider());
        await svc.CreateRepoAsync("r");
        var e = await Rejected(() => svc.CommitAsync("r", TestData.Commit("layers only", null, TestData.Layer(Changes.NewId(), "Default"))), 422, ErrorCodes.ValidationFailed);
        HasDetail(e, ErrorCodes.SettingsCount);
        e = await Rejected(() => svc.CommitAsync("r", TestData.Commit("settings only", null, TestData.Settings())), 422, ErrorCodes.ValidationFailed);
        HasDetail(e, ErrorCodes.NoLayers);
    }

    [Test]
    public async Task IdenticalContentSharesOneVersion()
    {
        var s = await Scenario.CreateAsync();
        var copy = Changes.NewId();
        var result = await s.CommitAsync("Copy box", TestData.Object(copy, s.Walls, s.BoxGeometry, "Box"));
        var original = await s.Service.GetEntityAsync(s.Repo, "main", "main", EntityKinds.Object, s.Box);
        Assert.Equal(original.Version, result.Versions.Single().Version, "a copy with the same content has the same version hash");
    }

    [Test]
    public async Task ObjectsMustReferenceAnExistingLayer()
    {
        var s = await Scenario.CreateAsync();
        var e = await Rejected(() => s.CommitAsync("dangling", TestData.Object(Changes.NewId(), Changes.NewId(), s.BoxGeometry)), 422, ErrorCodes.ValidationFailed);
        HasDetail(e, ErrorCodes.MissingRef);

        var noLayer = Changes.Put(EntityKinds.Object, Changes.NewId(), new ManifestInput { Components = new() { ["geometry"] = s.BoxGeometry } });
        e = await Rejected(() => s.CommitAsync("no layer", noLayer), 422, ErrorCodes.ValidationFailed);
        HasDetail(e, ErrorCodes.ObjectWithoutLayer);
    }

    [Test]
    public async Task ComponentsMustBeUploadedFirst()
    {
        var s = await Scenario.CreateAsync();
        var notUploaded = ContentHash.Compute(TestData.Geometry("never uploaded"));
        var e = await Rejected(() => s.CommitAsync("missing blob", TestData.Object(Changes.NewId(), s.Walls, notUploaded)), 422, ErrorCodes.ValidationFailed);
        HasDetail(e, ErrorCodes.ObjectMissing);
    }

    [Test]
    public async Task ReferencedLayersCannotBeDeleted()
    {
        var s = await Scenario.CreateAsync();
        var e = await Rejected(() => s.CommitAsync("delete walls", Changes.Delete(EntityKinds.Layer, s.Walls)), 422, ErrorCodes.ValidationFailed);
        HasDetail(e, ErrorCodes.DanglingRef);

        await s.CommitAsync("delete walls and its objects", Changes.Delete(EntityKinds.Layer, s.Walls), Changes.Delete(EntityKinds.Object, s.Box));
        var left = await s.Service.ListEntitiesAsync(s.Repo, "main", null, null, false);
        Assert.Equal(2, left.Entities.Count);
    }

    [Test]
    public async Task SiblingLayerNamesAreUniqueIgnoringCase()
    {
        var s = await Scenario.CreateAsync();
        var e = await Rejected(() => s.CommitAsync("dup", TestData.Layer(Changes.NewId(), "WALLS")), 422, ErrorCodes.ValidationFailed);
        HasDetail(e, ErrorCodes.DuplicateLayerName);

        await s.CommitAsync("same name, other parent", TestData.Layer(Changes.NewId(), "Walls", parentId: s.Walls));
    }

    [Test]
    public async Task LayerParentsCannotFormACycle()
    {
        var s = await Scenario.CreateAsync();
        var child = Changes.NewId();
        await s.CommitAsync("child", TestData.Layer(child, "Child", parentId: s.Walls));
        var e = await Rejected(() => s.CommitAsync("cycle", TestData.Layer(s.Walls, "Walls", parentId: child)), 422, ErrorCodes.ValidationFailed);
        HasDetail(e, ErrorCodes.LayerCycle);
    }

    [Test]
    public async Task StaleHeadIsRejectedWithCurrentHead()
    {
        var s = await Scenario.CreateAsync();
        var first = s.Head;
        await s.CommitAsync("rename", TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Box renamed"));
        var e = await Rejected(() => s.Service.CommitAsync(s.Repo, TestData.Commit("stale", first, TestData.Layer(Changes.NewId(), "Late"))), 409, ErrorCodes.RefConflict);
        Assert.Equal(s.Head, e.Details!["current"]);
    }

    [Test]
    public async Task CommitsThatChangeNothingAreRejected()
    {
        var s = await Scenario.CreateAsync();
        await Rejected(() => s.CommitAsync("noop", TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Box")), 422, ErrorCodes.NoChanges);
    }

    [Test]
    public async Task MalformedRequestsAreRejectedWithDetails()
    {
        var s = await Scenario.CreateAsync();
        var e = await Rejected(() => s.CommitAsync("bad",
            Changes.Delete("teapot", Changes.NewId()),
            Changes.Delete(EntityKinds.Layer, "NOT-A-GUID"),
            Changes.Delete(EntityKinds.Object, s.Box),
            Changes.Delete(EntityKinds.Object, s.Box)), 400, ErrorCodes.InvalidRequest);
        HasDetail(e, ErrorCodes.UnknownKind);
        HasDetail(e, ErrorCodes.InvalidEntityId);
        HasDetail(e, ErrorCodes.DuplicateChange);

        await Rejected(() => s.Service.CommitAsync(s.Repo, TestData.Commit("no author", s.Head, Changes.Delete(EntityKinds.Object, s.Box)) with { Author = new AuthorInfo() }), 400, ErrorCodes.InvalidRequest);
    }

    [Test]
    public async Task OldVersionsCanBeRestoredByHash()
    {
        var s = await Scenario.CreateAsync();
        var original = (await s.Service.GetEntityAsync(s.Repo, "main", "main", EntityKinds.Object, s.Box)).Version;
        await s.CommitAsync("rename", TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Renamed"));
        var restored = await s.CommitAsync("restore", Changes.Restore(EntityKinds.Object, s.Box, original));
        Assert.Equal(original, restored.Versions.Single().Version);

        var e = await Rejected(() => s.CommitAsync("not a manifest", Changes.Restore(EntityKinds.Object, s.Box, s.BoxGeometry)), 422, ErrorCodes.ValidationFailed);
        HasDetail(e, ErrorCodes.ObjectMissing);
    }

    [Test]
    public async Task DiffNamesTheChangedComponents()
    {
        var s = await Scenario.CreateAsync();
        var before = s.Head;
        var newGeometry = await s.UploadAsync(TestData.Geometry("box v2"));
        await s.CommitAsync("edit", TestData.Object(s.Box, s.Layer0, newGeometry, "Box"));
        var diff = await s.Service.DiffAsync(s.Repo, before, "main");
        var entry = Assert.Single(diff.Changes);
        Assert.Equal(DiffChanges.Modified, entry.Change);
        Assert.SequenceEqual(["geometry"], entry.Components!);
        Assert.True(entry.RefsChanged == true, "moved to another layer");

        var full = await s.Service.DiffAsync(s.Repo, null, before);
        Assert.Equal(4, full.Changes.Count(c => c.Change == DiffChanges.Added));
    }

    [Test]
    public async Task UploadsAreVerifiedAgainstTheirHash()
    {
        var s = await Scenario.CreateAsync();
        var data = TestData.Geometry("x");
        var e = await Rejected(() => s.Service.PutObjectAsync(s.Repo, ContentHash.Compute(TestData.Geometry("y")), data), 400, ErrorCodes.HashMismatch);
        Assert.Equal(ContentHash.Compute(data), e.Details!["actual"]);
        var put = await s.Service.PutObjectAsync(s.Repo, ContentHash.Compute(data), data);
        Assert.True(put.Created);
        Assert.False((await s.Service.PutObjectAsync(s.Repo, ContentHash.Compute(data), data)).Created, "uploads are idempotent");
    }

    [Test]
    public async Task NotesAndAgentMetadataNeedNoGeometry()
    {
        var s = await Scenario.CreateAsync();
        var note = Changes.Put(EntityKinds.Note, Changes.NewId(), new ManifestInput
        {
            Attributes = new JsonObject { ["title"] = "Facade study", ["markdown"] = "Try 1.2 m panels on the south side." },
        });
        await s.BranchAsync("agent/notes");
        var result = await s.Service.CommitAsync(s.Repo, TestData.Commit("agent/notes", "Add note", s.Head, new[] { note }) with
        {
            Author = TestData.Bot, Meta = new() { ["agent.session"] = "abc123" },
        });
        var info = await s.Service.GetCommitAsync(s.Repo, result.Commit);
        Assert.Equal(AuthorKinds.Agent, info.Author.Kind);
        Assert.Equal("abc123", info.Meta!["agent.session"]);
    }
}
