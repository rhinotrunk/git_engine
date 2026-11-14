using System.Text.Json;
using System.Text.Json.Nodes;
using RhinoTrunk.Contracts;
using RhinoTrunk.Core.Storage;

namespace RhinoTrunk.Core;

/// <summary>A change after resolution: <see cref="Version"/> null means delete.</summary>
public sealed record ResolvedChange(EntityKey Key, string? Version, Manifest? Manifest);

/// <summary>
/// Tier-1 structural invariants, enforced on every commit and merge without any Rhino library:
/// manifest integrity, reference integrity, no dangling references after deletes, exactly one settings
/// entity and at least one layer per document, unique sibling layer names, no layer cycles,
/// and every object on exactly one layer.
/// </summary>
public sealed class SnapshotValidator(IObjectStore objects, TreeStore trees, ManifestCache manifests)
{
    public async Task<List<ApiErrorDetail>> ValidateAsync(string newRoot, IReadOnlyList<ResolvedChange> changes, CancellationToken ct = default)
    {
        var errors = new List<ApiErrorDetail>();

        // 1. Each new version is well formed and its references resolve in the new snapshot.
        foreach (var change in changes.Where(c => c.Version is not null))
        {
            var (key, manifest) = (change.Key, change.Manifest!);
            await ValidateManifestAsync(key, manifest, errors, ct);
            foreach (var r in manifest.Refs)
            {
                if (!EntityKinds.IsKnown(r.Kind) || !Names.IsValidEntityId(r.Kind, r.Id)) continue; // reported above
                if (await trees.GetAsync(newRoot, new EntityKey(key.Doc, r.Kind, r.Id), ct) is null)
                    errors.Add(Detail(ErrorCodes.MissingRef, $"{key.Kind} {key.Id} references {r.Kind} {r.Id} ({r.Role}), which does not exist.", key));
            }
        }

        // 2. Deleted entities must not be referenced by anything left in the document.
        var deletedByDoc = changes.Where(c => c.Version is null).GroupBy(c => c.Key.Doc, StringComparer.Ordinal);
        foreach (var group in deletedByDoc)
        {
            var deleted = group.Select(c => (c.Key.Kind, c.Key.Id)).ToHashSet();
            foreach (var (key, version) in await trees.ListAsync(newRoot, group.Key, null, ct))
            {
                var manifest = await manifests.GetAsync(version, ct);
                if (manifest is null) continue;
                foreach (var r in manifest.Refs.Where(r => deleted.Contains((r.Kind, r.Id))))
                    errors.Add(Detail(ErrorCodes.DanglingRef, $"Cannot delete {r.Kind} {r.Id}: still referenced by {key.Kind} {key.Id} ({r.Role}).", key));
            }
        }

        // 3. Document-level invariants for every touched document.
        foreach (var doc in changes.Select(c => c.Key.Doc).Distinct(StringComparer.Ordinal))
        {
            var settings = await trees.CountAsync(newRoot, doc, EntityKinds.Settings, ct);
            if (settings != 1)
                errors.Add(new ApiErrorDetail { Code = ErrorCodes.SettingsCount, Doc = doc, Message = $"Document '{doc}' must contain exactly one settings entity (found {settings})." });
            if (await trees.CountAsync(newRoot, doc, EntityKinds.Layer, ct) == 0)
                errors.Add(new ApiErrorDetail { Code = ErrorCodes.NoLayers, Doc = doc, Message = $"Document '{doc}' must contain at least one layer." });
            if (changes.Any(c => c.Key.Doc == doc && c.Key.Kind == EntityKinds.Layer))
                await ValidateLayersAsync(newRoot, doc, errors, ct);
        }
        return errors;
    }

    private async Task ValidateManifestAsync(EntityKey key, Manifest manifest, List<ApiErrorDetail> errors, CancellationToken ct)
    {
        if (manifest.Kind != key.Kind)
            errors.Add(Detail(ErrorCodes.KindMismatch, $"Manifest kind '{manifest.Kind}' does not match entity kind '{key.Kind}'.", key));

        foreach (var (name, hash) in manifest.Components)
        {
            if (!Names.IsValidToken(name))
                errors.Add(Detail(ErrorCodes.InvalidManifest, $"Invalid component name '{name}'.", key));
            if (!ContentHash.IsValid(hash))
                errors.Add(Detail(ErrorCodes.InvalidManifest, $"Component '{name}' has an invalid hash.", key));
            else if (!await objects.ExistsAsync(hash, ct))
                errors.Add(Detail(ErrorCodes.ObjectMissing, $"Component '{name}' references object {hash}, which has not been uploaded.", key));
        }

        foreach (var r in manifest.Refs)
        {
            if (!EntityKinds.IsKnown(r.Kind))
                errors.Add(Detail(ErrorCodes.UnknownKind, $"Reference to unknown kind '{r.Kind}'.", key));
            else if (!Names.IsValidEntityId(r.Kind, r.Id))
                errors.Add(Detail(ErrorCodes.InvalidEntityId, $"Reference to invalid {r.Kind} id '{r.Id}'.", key));
            if (!Names.IsValidToken(r.Role))
                errors.Add(Detail(ErrorCodes.InvalidManifest, $"Invalid reference role '{r.Role}'.", key));
        }

        if (key.Kind == EntityKinds.Object)
        {
            var layers = manifest.Refs.Count(r => r.Role == RefRoles.Layer && r.Kind == EntityKinds.Layer);
            if (layers != 1 || manifest.Refs.Any(r => r.Role == RefRoles.Layer && r.Kind != EntityKinds.Layer))
                errors.Add(Detail(ErrorCodes.ObjectWithoutLayer, "An object must reference exactly one layer (role 'layer').", key));
        }

        if (key.Kind == EntityKinds.Layer)
        {
            var parents = manifest.Refs.Where(r => r.Role == RefRoles.Parent).ToList();
            if (parents.Count > 1 || parents.Any(p => p.Kind != EntityKinds.Layer))
                errors.Add(Detail(ErrorCodes.InvalidManifest, "A layer has at most one parent, which must be a layer.", key));
            if (!manifest.Components.ContainsKey("attributes"))
                errors.Add(Detail(ErrorCodes.InvalidLayerName, "A layer needs an 'attributes' component with a name.", key));
        }
    }

    private async Task ValidateLayersAsync(string root, string doc, List<ApiErrorDetail> errors, CancellationToken ct)
    {
        var parentOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        var nameOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, version) in await trees.ListAsync(root, doc, EntityKinds.Layer, ct))
        {
            var manifest = await manifests.GetAsync(version, ct);
            if (manifest is null) continue;
            parentOf[key.Id] = manifest.Refs.FirstOrDefault(r => r.Role == RefRoles.Parent)?.Id;
            var name = await ReadNameAsync(manifest, ct);
            if (string.IsNullOrWhiteSpace(name) || name.Contains("::", StringComparison.Ordinal))
                errors.Add(Detail(ErrorCodes.InvalidLayerName, $"Layer {key.Id} has a missing or invalid name.", key));
            else
                nameOf[key.Id] = name;
        }

        // Sibling names must be unique (case-insensitive, like Rhino; see OPEN_QUESTIONS.md).
        foreach (var group in nameOf.GroupBy(p => (Parent: parentOf.GetValueOrDefault(p.Key) ?? "", Name: p.Value.ToLowerInvariant())))
        {
            if (group.Count() < 2) continue;
            foreach (var layer in group.OrderBy(l => l.Key, StringComparer.Ordinal))
                errors.Add(Detail(ErrorCodes.DuplicateLayerName, $"Layer name '{layer.Value}' is used more than once under the same parent.", new EntityKey(doc, EntityKinds.Layer, layer.Key)));
        }

        // Parent chains must terminate.
        foreach (var start in parentOf.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var current = parentOf[start];
            var steps = 0;
            while (current is not null && parentOf.ContainsKey(current) && steps++ <= parentOf.Count)
            {
                if (current == start)
                {
                    errors.Add(Detail(ErrorCodes.LayerCycle, $"Layer {start} is part of a parent cycle.", new EntityKey(doc, EntityKinds.Layer, start)));
                    break;
                }
                current = parentOf[current];
            }
        }
    }

    private async Task<string?> ReadNameAsync(Manifest manifest, CancellationToken ct)
    {
        if (!manifest.Components.TryGetValue("attributes", out var hash)) return null;
        var bytes = await objects.GetAsync(hash, ct);
        if (bytes is null) return null;
        try
        {
            return JsonNode.Parse(bytes) is JsonObject obj && obj["name"] is JsonValue v && v.TryGetValue<string>(out var name) ? name : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ApiErrorDetail Detail(string code, string message, EntityKey key) =>
        new() { Code = code, Message = message, Doc = key.Doc, Kind = key.Kind, Id = key.Id };
}

/// <summary>Parsed manifests by hash. Manifests are immutable, so caching is always safe.</summary>
public sealed class ManifestCache(IObjectStore objects)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Manifest> _cache = new(StringComparer.Ordinal);

    public async Task<Manifest?> GetAsync(string hash, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(hash, out var m)) return m;
        var manifest = await objects.GetTypedJsonAsync<Manifest>(hash, "manifest", ct);
        if (manifest is null) return null;
        _cache[hash] = manifest;
        return manifest;
    }
}
