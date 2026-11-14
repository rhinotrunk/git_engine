using System.Text;
using System.Text.Json.Nodes;
using RhinoTrunk.Contracts;
using RhinoTrunk.Core;
using RhinoTrunk.Core.Storage;

namespace RhinoTrunk.Tests;

public sealed class CanonicalJsonTests
{
    [Test]
    public void SortsMembersAndDropsWhitespace()
    {
        var bytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes("{ \"b\": 1, \"a\": [true, null, \"x\", {\"z\":1,\"y\":2}] }"));
        Assert.Equal("{\"a\":[true,null,\"x\",{\"y\":2,\"z\":1}],\"b\":1}", Encoding.UTF8.GetString(bytes));
    }

    [Test]
    public void FormatsNumbersLikeEcmaScript()
    {
        var cases = new (double Value, string Expected)[]
        {
            (0, "0"), (-0.0, "0"), (1, "1"), (100, "100"), (1.5, "1.5"), (-123.456, "-123.456"),
            (0.001, "0.001"), (0.000001, "0.000001"), (1e-7, "1e-7"), (1e20, "100000000000000000000"),
            (1e21, "1e+21"), (1.25e22, "1.25e+22"), (0.1 + 0.2, "0.30000000000000004"), (9007199254740991, "9007199254740991"),
        };
        foreach (var (value, expected) in cases)
            Assert.Equal(expected, CanonicalJson.FormatNumber(value), $"formatting {value:R}");
    }

    [Test]
    public void EscapesOnlyWhatJsonRequires()
    {
        var node = new JsonObject { ["s"] = "a\"b\\c\n\u0001é/" };
        Assert.Equal("{\"s\":\"a\\\"b\\\\c\\n\\u0001é/\"}", CanonicalJson.SerializeToString(node));
    }

    [Test]
    public void DetectsCanonicalForm()
    {
        Assert.True(CanonicalJson.IsCanonical(Encoding.UTF8.GetBytes("{\"a\":1,\"b\":[2]}")));
        Assert.False(CanonicalJson.IsCanonical(Encoding.UTF8.GetBytes("{\"b\":1,\"a\":2}")));
        Assert.False(CanonicalJson.IsCanonical(Encoding.UTF8.GetBytes("{\"a\": 1}")));
        Assert.False(CanonicalJson.IsCanonical(Encoding.UTF8.GetBytes("not json")));
    }

    [Test]
    public void ContentHashMatchesSha256()
    {
        Assert.Equal("sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ContentHash.Compute("abc"u8));
        Assert.True(ContentHash.IsValid(ContentHash.Compute([])));
        Assert.False(ContentHash.IsValid("sha256:ABC"));
        Assert.False(ContentHash.IsValid("md5:900150983cd24fb0d6963f7d28e17f72"));
    }

    [Test]
    public void EntityIdsAreLowercaseGuids()
    {
        Assert.True(Names.IsValidEntityId(EntityKinds.Layer, "6b1f0c3e-6a2b-4c5d-9e8f-0a1b2c3d4e5f"));
        Assert.False(Names.IsValidEntityId(EntityKinds.Layer, "6B1F0C3E-6A2B-4C5D-9E8F-0A1B2C3D4E5F"));
        Assert.False(Names.IsValidEntityId(EntityKinds.Layer, "{6b1f0c3e-6a2b-4c5d-9e8f-0a1b2c3d4e5f}"));
        Assert.True(Names.IsValidEntityId(EntityKinds.Settings, "settings"));
        Assert.False(Names.IsValidEntityId(EntityKinds.Settings, Guid.NewGuid().ToString()));
        Assert.True(Names.IsValidRefName("agent/facade-study"));
        Assert.False(Names.IsValidRefName("bad..name"));
        Assert.False(Names.IsValidRefName("trailing/"));
    }
}

public sealed class TreeStoreTests
{
    private readonly InMemoryObjectStore _objects = new();
    private readonly TreeStore _trees;

    public TreeStoreTests() => _trees = new TreeStore(_objects);

    private static string V(int i) => ContentHash.Compute(Encoding.UTF8.GetBytes("version " + i));

    private static EntityKey K(string kind, int i) => new("main", kind, new Guid(i, 0, 0, new byte[8]).ToString("D"));

    [Test]
    public async Task PutGetAndDelete()
    {
        var empty = await _trees.EmptyRootAsync();
        var root = await _trees.ApplyAsync(empty, [new TreeChange(K("object", 1), V(1)), new TreeChange(K("layer", 2), V(2))]);
        Assert.Equal(V(1), await _trees.GetAsync(root, K("object", 1)));
        Assert.Equal(V(2), await _trees.GetAsync(root, K("layer", 2)));
        Assert.Null(await _trees.GetAsync(root, K("object", 3)));

        var deleted = await _trees.ApplyAsync(root, [new TreeChange(K("object", 1), null), new TreeChange(K("layer", 2), null)]);
        Assert.Equal(empty, deleted, "removing everything gives the empty root again");
    }

    [Test]
    public async Task SameContentGivesSameRootRegardlessOfOrder()
    {
        var empty = await _trees.EmptyRootAsync();
        var changes = Enumerable.Range(0, 300).Select(i => new TreeChange(K(i % 3 == 0 ? "layer" : "object", i), V(i))).ToList();

        var oneShot = await _trees.ApplyAsync(empty, changes);
        var stepwise = empty;
        foreach (var batch in changes.AsEnumerable().Reverse().Chunk(37)) stepwise = await _trees.ApplyAsync(stepwise, batch);
        Assert.Equal(oneShot, stepwise);
    }

    [Test]
    public async Task ChangingOneEntityRewritesOnlyItsPath()
    {
        var empty = await _trees.EmptyRootAsync();
        var a = await _trees.ApplyAsync(empty, Enumerable.Range(0, 5000).Select(i => new TreeChange(K("object", i), V(i))));
        var before = _objects.Count;
        var b = await _trees.ApplyAsync(a, [new TreeChange(K("object", 42), V(-42))]);
        Assert.Equal(4, _objects.Count - before, "one leaf, one kind node, one doc node, one root");

        var diff = await _trees.DiffAsync(a, b);
        var entry = Assert.Single(diff);
        Assert.Equal(K("object", 42), entry.Key);
        Assert.Equal(V(42), entry.From);
        Assert.Equal(V(-42), entry.To);
    }

    [Test]
    public async Task DiffReportsAddsDeletesAndModifications()
    {
        var empty = await _trees.EmptyRootAsync();
        var a = await _trees.ApplyAsync(empty, [new TreeChange(K("object", 1), V(1)), new TreeChange(K("object", 2), V(2))]);
        var b = await _trees.ApplyAsync(a, [new TreeChange(K("object", 1), null), new TreeChange(K("object", 2), V(20)), new TreeChange(K("layer", 3), V(3))]);
        var diff = await _trees.DiffAsync(a, b);
        Assert.Equal(3, diff.Count);
        Assert.Contains(diff, d => d.Key == K("object", 1) && d.From == V(1) && d.To is null);
        Assert.Contains(diff, d => d.Key == K("object", 2) && d.From == V(2) && d.To == V(20));
        Assert.Contains(diff, d => d.Key == K("layer", 3) && d.From is null && d.To == V(3));
        Assert.Equal(2, (await _trees.DiffAsync(empty, a)).Count);
    }
}
