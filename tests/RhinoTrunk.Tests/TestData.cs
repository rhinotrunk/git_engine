using System.Text;
using System.Text.Json.Nodes;
using RhinoTrunk.Client;
using RhinoTrunk.Contracts;
using RhinoTrunk.Core;
using RhinoTrunk.Core.Storage;

namespace RhinoTrunk.Tests;

/// <summary>Builders for typical document content. Geometry is opaque bytes to the server, so fakes are fine here.</summary>
public static class TestData
{
    public static readonly AuthorInfo Alex = new() { Id = "alex", Name = "Alex", Kind = AuthorKinds.Human };
    public static readonly AuthorInfo Bot = new() { Id = "agent:facade-bot", Name = "Facade bot", Kind = AuthorKinds.Agent };

    public const string Repo = "demo";

    public static byte[] Geometry(string seed) => Encoding.UTF8.GetBytes("openNURBS archive bytes for " + seed);

    public static Change Settings(string units = "Millimeters") =>
        Changes.Put(EntityKinds.Settings, Names.SettingsId, new ManifestInput
        {
            Attributes = new JsonObject { ["unitSystem"] = units, ["absoluteTolerance"] = 0.001, ["angleToleranceRadians"] = 0.0174533 },
        });

    public static Change Layer(string id, string name, string? parentId = null, string color = "#000000") =>
        Changes.Put(EntityKinds.Layer, id, new ManifestInput
        {
            Attributes = new JsonObject { ["name"] = name, ["color"] = color, ["visible"] = true, ["locked"] = false },
            Refs = parentId is null ? [] : [Changes.Ref(EntityKinds.Layer, parentId, RefRoles.Parent)],
        });

    public static Change Object(string id, string layerId, string geometryHash, string name = "", string subtype = "Brep") =>
        Changes.Put(EntityKinds.Object, id, new ManifestInput
        {
            Subtype = subtype,
            Attributes = new JsonObject { ["name"] = name, ["colorSource"] = "layer" },
            Components = new() { ["geometry"] = geometryHash },
            Refs = [Changes.Ref(EntityKinds.Layer, layerId, RefRoles.Layer)],
        });

    public static CommitRequest Commit(string message, string? expectedHead, params Change[] changes) => Commit("main", message, expectedHead, changes);

    public static CommitRequest Commit(string branch, string message, string? expectedHead, params Change[] changes) => new()
    {
        Branch = branch, ExpectedHead = expectedHead, Message = message, Author = Alex, Changes = changes.ToList(),
    };
}

/// <summary>A service over in-memory storage with a repository and a valid first commit.</summary>
public sealed class Scenario
{
    public RhinoTrunkService Service { get; }
    public string Repo => TestData.Repo;
    public string Layer0 { get; } = Changes.NewId();
    public string Walls { get; } = Changes.NewId();
    public string Box { get; } = Changes.NewId();
    public string BoxGeometry { get; private set; } = "";
    public string Head { get; set; } = "";

    private Scenario(IRepositoryProvider provider) => Service = new RhinoTrunkService(provider);

    public static async Task<Scenario> CreateAsync(IRepositoryProvider? provider = null)
    {
        var s = new Scenario(provider ?? new InMemoryRepositoryProvider());
        await s.Service.CreateRepoAsync(TestData.Repo);
        s.BoxGeometry = await s.UploadAsync(TestData.Geometry("box"));
        var result = await s.Service.CommitAsync(s.Repo, TestData.Commit("Initial document", null,
            TestData.Settings(),
            TestData.Layer(s.Layer0, "Default"),
            TestData.Layer(s.Walls, "Walls"),
            TestData.Object(s.Box, s.Walls, s.BoxGeometry, "Box")));
        s.Head = result.Commit;
        return s;
    }

    public async Task<string> UploadAsync(byte[] data)
    {
        var hash = ContentHash.Compute(data);
        await Service.PutObjectAsync(Repo, hash, data);
        return hash;
    }

    public async Task<CommitResponse> CommitAsync(string message, params Change[] changes)
    {
        var result = await Service.CommitAsync(Repo, TestData.Commit(message, Head, changes));
        Head = result.Commit;
        return result;
    }

    public async Task<CommitResponse> CommitOnAsync(string branch, string? expected, string message, params Change[] changes) =>
        await Service.CommitAsync(Repo, TestData.Commit(branch, message, expected, changes));

    public async Task<string> BranchAsync(string name, string? from = null)
    {
        var at = from ?? Head;
        await Service.UpdateRefAsync(Repo, RefTypes.Branch, name, new RefUpdateRequest { Commit = at, Expected = null });
        return at;
    }
}
