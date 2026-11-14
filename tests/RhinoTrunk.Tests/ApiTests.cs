using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RhinoTrunk.Client;
using RhinoTrunk.Contracts;
using RhinoTrunk.Server;

namespace RhinoTrunk.Tests;

/// <summary>Runs the real server on a random local port and talks to it through the client SDK.</summary>
public sealed class ApiServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string? _dataDir;

    private ApiServer(WebApplication app, Uri address, string? dataDir)
    {
        _app = app;
        _dataDir = dataDir;
        Http = new HttpClient { BaseAddress = address };
        Client = new RhinoTrunkClient(Http);
    }

    public HttpClient Http { get; }
    public RhinoTrunkClient Client { get; }

    public static async Task<ApiServer> StartAsync(bool fileSystem = false)
    {
        var dataDir = fileSystem ? Path.Combine(Path.GetTempPath(), "rhinotrunk-api-" + Guid.NewGuid().ToString("N")) : null;
        var app = ServerApp.Build([], b =>
        {
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Configuration["Storage:Provider"] = fileSystem ? "FileSystem" : "InMemory";
            if (dataDir is not null) b.Configuration["Storage:Root"] = dataDir;
            b.Logging.ClearProviders();
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new ApiServer(app, new Uri(address), dataDir);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (_dataDir is not null && Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }
}

public sealed class ApiTests
{
    [Test]
    public Task EndToEndWithInMemoryStorage() => EndToEndAsync(fileSystem: false);

    [Test]
    public Task EndToEndWithFileSystemStorage() => EndToEndAsync(fileSystem: true);

    /// <summary>The workflow a Rhino client or agent goes through, entirely over HTTP.</summary>
    private static async Task EndToEndAsync(bool fileSystem)
    {
        await using var server = await ApiServer.StartAsync(fileSystem);
        var api = server.Client;
        const string repo = "tower";
        await api.CreateRepoAsync(repo);

        // 1. Upload geometry blobs; only missing ones are sent.
        var blobs = new[] { TestData.Geometry("slab"), TestData.Geometry("core") };
        var hashes = await api.UploadAsync(repo, blobs);
        Assert.Equal(0, (await api.MissingAsync(repo, hashes)).Count);

        // 2. First commit: settings, layers, objects.
        var (defaultLayer, structure, slab, core) = (Changes.NewId(), Changes.NewId(), Changes.NewId(), Changes.NewId());
        var first = await api.CommitAsync(repo, TestData.Commit("Initial model", null,
            TestData.Settings(), TestData.Layer(defaultLayer, "Default"), TestData.Layer(structure, "Structure"),
            TestData.Object(slab, structure, hashes[0], "Slab"), TestData.Object(core, structure, hashes[1], "Core", "Extrusion")));
        Assert.Equal(5, first.Versions.Count);

        // 3. Read the snapshot back (what a checkout does).
        var entities = await api.ListEntitiesAsync(repo, "main", includeManifests: true);
        Assert.Equal(5, entities.Entities.Count);
        var slabEntity = Assert.Single(entities.Entities, e => e.Id == slab);
        var attributes = await api.GetJsonObjectAsync(repo, slabEntity.Manifest!.Components["attributes"]);
        Assert.Equal("Slab", attributes!["name"]!.GetValue<string>());
        var objects = await api.BatchGetAsync(repo, [slabEntity.Manifest.Components["geometry"]]);
        Assert.Equal("openNURBS archive bytes for slab", Encoding.UTF8.GetString(objects.Single().Value));
        var layers = await api.ListEntitiesAsync(repo, "main", kind: EntityKinds.Layer);
        Assert.Equal(2, layers.Entities.Count);

        // 4. An agent works on a branch whose name contains a slash.
        await api.SetBranchAsync(repo, "agent/facade", first.Commit, expected: null);
        var panel = Changes.NewId();
        var panelGeometry = await api.PutObjectAsync(repo, TestData.Geometry("panel"));
        var agentCommit = await api.CommitAsync(repo, new CommitRequest
        {
            Branch = "agent/facade", ExpectedHead = first.Commit, Message = "Add facade panel", Author = TestData.Bot,
            Meta = new() { ["agent.model"] = "test-model" },
            Changes = [TestData.Object(panel, structure, panelGeometry, "Panel")],
        });
        Assert.Equal(agentCommit.Commit, (await api.GetBranchAsync(repo, "agent/facade"))!.Commit);
        Assert.Equal(agentCommit.Commit, (await api.GetCommitAsync(repo, "agent/facade")).Id, "branch names with '/' resolve as revisions");

        // 5. Meanwhile a human edits main.
        var human = await api.CommitAsync(repo, TestData.Commit("Rename core", first.Commit,
            TestData.Object(core, structure, hashes[1], "Core A", "Extrusion")));

        // 6. Merge, then inspect graph, diff and history.
        var merge = await api.MergeAsync(repo, new MergeRequest { Branch = "main", ExpectedHead = human.Commit, Theirs = "agent/facade", Author = TestData.Alex });
        Assert.Equal(MergeStatuses.Merged, merge.Status);
        var graph = await api.GraphAsync(repo);
        Assert.Equal(4, graph.Commits.Count);
        Assert.Equal(2, graph.Commits[0].Parents.Count);
        var diff = await api.DiffAsync(repo, first.Commit, "main");
        Assert.SequenceEqual(new[] { core, panel }.OrderBy(x => x, StringComparer.Ordinal), diff.Changes.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(DiffChanges.Added, diff.Changes.Single(c => c.Id == panel).Change);
        var history = await api.HistoryAsync(repo, EntityKinds.Object, core);
        Assert.Equal(2, history.Entries.Count);

        // 7. Tags, annotations and derived data.
        await api.CreateTagAsync(repo, "review-1", merge.Commit);
        await api.AddAnnotationAsync(repo, "review-1", new Annotation { Namespace = "ci/geometry-valid", Status = AnnotationStatuses.Success });
        Assert.Equal(AnnotationStatuses.Success, (await api.ListAnnotationsAsync(repo, merge.Commit)).Annotations.Single().Status);
        var preview = await api.PutObjectAsync(repo, "preview mesh"u8.ToArray());
        await api.SetDerivedAsync(repo, panelGeometry, "preview-mesh.v1", preview);
        Assert.Equal(preview, (await api.GetDerivedAsync(repo, panelGeometry, "preview-mesh.v1"))!.Result);
        Assert.Null(await api.GetDerivedAsync(repo, hashes[0], "preview-mesh.v1"));

        var refs = (await api.ListRefsAsync(repo)).Refs.Select(r => r.Name).ToList();
        Assert.SequenceEqual(["agent/facade", "main", "review-1"], refs);
    }

    [Test]
    public async Task ErrorsUseTheApiErrorShape()
    {
        await using var server = await ApiServer.StartAsync();
        var api = server.Client;
        await api.CreateRepoAsync("r");

        var notFound = await Assert.ThrowsAsync<RhinoTrunkApiException>(() => api.GetRepoAsync("nope"));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, notFound.Error.Code);

        var exists = await Assert.ThrowsAsync<RhinoTrunkApiException>(() => api.CreateRepoAsync("r"));
        Assert.Equal(ErrorCodes.RepoExists, exists.Error.Code);

        using var wrong = new ByteArrayContent("abc"u8.ToArray());
        var response = await server.Http.PutAsync($"repos/r/objects/{ContentHash.Compute("xyz"u8)}", wrong);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.HashMismatch, (await response.Content.ReadFromJsonAsync<ApiError>(RhinoTrunkJson.Options))!.Code);

        var invalid = await Assert.ThrowsAsync<RhinoTrunkApiException>(() => api.CommitAsync("r", TestData.Commit("layers only", null, TestData.Layer(Changes.NewId(), "Default"))));
        Assert.Equal((HttpStatusCode)422, invalid.StatusCode);
        Assert.Equal(ErrorCodes.ValidationFailed, invalid.Error.Code);
        Assert.Contains(invalid.Error.Errors!, d => d.Code == ErrorCodes.SettingsCount);

        var first = await api.CommitAsync("r", TestData.Commit("init", null, TestData.Settings(), TestData.Layer(Changes.NewId(), "Default")));
        var stale = await Assert.ThrowsAsync<RhinoTrunkApiException>(() => api.CommitAsync("r", TestData.Commit("stale", null, TestData.Layer(Changes.NewId(), "Other"))));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ErrorCodes.RefConflict, stale.Error.Code);
        Assert.Equal(first.Commit, stale.Error.Data!["current"]);

        using var malformed = new StringContent("{ not json", Encoding.UTF8, "application/json");
        response = await server.Http.PostAsync("repos/r/commits", malformed);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidRequest, (await response.Content.ReadFromJsonAsync<ApiError>(RhinoTrunkJson.Options))!.Code);

        using var nullChanges = new StringContent("{\"branch\":\"main\",\"author\":{\"id\":\"x\"},\"changes\":null}", Encoding.UTF8, "application/json");
        response = await server.Http.PostAsync("repos/r/commits", nullChanges);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidRequest, (await response.Content.ReadFromJsonAsync<ApiError>(RhinoTrunkJson.Options))!.Code);
    }

    [Test]
    public async Task RawJsonWorksWithoutTheSdk()
    {
        // What a Python or JavaScript agent would send: plain JSON, inline attributes, no hashing on the client.
        await using var server = await ApiServer.StartAsync();
        await server.Client.CreateRepoAsync("raw");
        var layerId = Guid.NewGuid().ToString();
        var body = new JsonObject
        {
            ["branch"] = "agent/py",
            ["expectedHead"] = null,
            ["message"] = "init from raw JSON",
            ["author"] = new JsonObject { ["id"] = "agent:py", ["kind"] = "agent" },
            ["changes"] = new JsonArray
            {
                new JsonObject { ["op"] = "put", ["kind"] = "settings", ["id"] = "settings", ["manifest"] = new JsonObject { ["attributes"] = new JsonObject { ["unitSystem"] = "Meters" } } },
                new JsonObject { ["op"] = "put", ["kind"] = "layer", ["id"] = layerId, ["manifest"] = new JsonObject { ["attributes"] = new JsonObject { ["name"] = "Default" } } },
            },
        };
        var response = await server.Http.PostAsJsonAsync("repos/raw/commits", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        var index = await server.Http.GetFromJsonAsync<JsonObject>("");
        Assert.Contains(index!["endpoints"]!.AsArray(), e => e!.GetValue<string>().Contains("/repos/{repo}/commits"));
    }
}
