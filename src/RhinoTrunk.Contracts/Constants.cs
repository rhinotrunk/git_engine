using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RhinoTrunk.Contracts;

/// <summary>Shared JSON settings for the wire format (camelCase, nulls omitted).</summary>
public static class RhinoTrunkJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Kinds of versioned entities. One entity per layer, material, object, etc.</summary>
public static class EntityKinds
{
    public const string Settings = "settings";             // singleton: units, tolerances, document user text
    public const string Layer = "layer";
    public const string Object = "object";                 // model geometry, annotations, block instances, block members
    public const string BlockDefinition = "blockDefinition";
    public const string Material = "material";             // render content (materials, environments, textures)
    public const string Linetype = "linetype";
    public const string DimensionStyle = "dimensionStyle";
    public const string HatchPattern = "hatchPattern";
    public const string Group = "group";
    public const string NamedView = "namedView";
    public const string Note = "note";                     // service-native: notes, specs, agent memos

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Settings, Layer, Object, BlockDefinition, Material, Linetype, DimensionStyle, HatchPattern, Group, NamedView, Note,
    };

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>Roles of references from one entity to another. Only <see cref="Layer"/> and <see cref="Parent"/> are interpreted by the server today.</summary>
public static class RefRoles
{
    public const string Layer = "layer";                     // object -> layer (exactly one, required)
    public const string Parent = "parent";                   // layer -> parent layer (at most one)
    public const string Material = "material";
    public const string Linetype = "linetype";
    public const string Group = "group";
    public const string BlockDefinition = "blockDefinition"; // block instance -> definition
    public const string Owner = "owner";                     // block member -> definition
    public const string DimensionStyle = "dimensionStyle";
    public const string HatchPattern = "hatchPattern";
}

public static class ChangeOps
{
    public const string Put = "put";
    public const string Delete = "delete";
}

public static class AuthorKinds
{
    public const string Human = "human";
    public const string Agent = "agent";
}

public static class RefTypes
{
    public const string Branch = "branch";
    public const string Tag = "tag";
}

public static class DiffChanges
{
    public const string Added = "added";
    public const string Deleted = "deleted";
    public const string Modified = "modified";
}

public static class MergeStatuses
{
    public const string Merged = "merged";
    public const string FastForward = "fastForward";
    public const string UpToDate = "upToDate";
}

public static class MergeTakes
{
    public const string Ours = "ours";
    public const string Theirs = "theirs";
}

public static class AnnotationStatuses
{
    public const string Pending = "pending";
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Info = "info";
}

/// <summary>Machine-readable error codes returned in <see cref="ApiError.Code"/> and <see cref="ApiErrorDetail.Code"/>.</summary>
public static class ErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string NotFound = "not_found";
    public const string RepoExists = "repo_exists";
    public const string HashMismatch = "hash_mismatch";
    public const string ObjectMissing = "object_missing";
    public const string RefConflict = "ref_conflict";             // stale expected head (compare-and-swap failed)
    public const string TagImmutable = "tag_immutable";
    public const string ValidationFailed = "validation_failed";
    public const string NoChanges = "no_changes";
    public const string MergeConflict = "merge_conflict";
    public const string InternalError = "internal_error";

    // validation details
    public const string UnknownKind = "unknown_kind";
    public const string InvalidEntityId = "invalid_entity_id";
    public const string DuplicateChange = "duplicate_change";
    public const string KindMismatch = "kind_mismatch";
    public const string EntityNotFound = "entity_not_found";
    public const string InvalidManifest = "invalid_manifest";
    public const string MissingRef = "missing_ref";               // reference target does not exist
    public const string DanglingRef = "dangling_ref";             // deleted entity is still referenced
    public const string ObjectWithoutLayer = "object_without_layer";
    public const string LayerCycle = "layer_cycle";
    public const string DuplicateLayerName = "duplicate_layer_name";
    public const string InvalidLayerName = "invalid_layer_name";
    public const string SettingsCount = "settings_count";
    public const string NoLayers = "no_layers";
    public const string Conflict = "conflict";
}

/// <summary>Naming rules shared by client and server.</summary>
public static partial class Names
{
    public const string DefaultDoc = "main";
    public const string DefaultBranch = "main";
    public const string SettingsId = "settings";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex RepoOrDocRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]{0,198}$")]
    private static partial Regex RefNameRegex();

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,63}$")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$")]
    private static partial Regex RecipeRegex();

    public static bool IsValidRepo(string? name) => name is not null && RepoOrDocRegex().IsMatch(name);
    public static bool IsValidDoc(string? name) => name is not null && RepoOrDocRegex().IsMatch(name);
    public static bool IsValidToken(string? name) => name is not null && TokenRegex().IsMatch(name);
    public static bool IsValidRecipe(string? name) => name is not null && RecipeRegex().IsMatch(name);

    [GeneratedRegex("^[a-z0-9][a-z0-9._/-]{0,99}$")]
    private static partial Regex NamespaceRegex();

    /// <summary>Annotation namespaces, e.g. "ci/geometry-valid" or "review".</summary>
    public static bool IsValidNamespace(string? name) => name is not null && NamespaceRegex().IsMatch(name);

    public static bool IsValidRefName(string? name) =>
        name is not null && RefNameRegex().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal)
        && !name.Contains("//", StringComparison.Ordinal) && !name.EndsWith('/') && !name.EndsWith('.')
        && !name.EndsWith(".lock", StringComparison.Ordinal);

    /// <summary>Entity ids are lowercase GUIDs ("D" format); the settings singleton uses <see cref="SettingsId"/>.</summary>
    public static bool IsValidEntityId(string kind, string? id)
    {
        if (id is null) return false;
        if (kind == EntityKinds.Settings) return id == SettingsId;
        return Guid.TryParseExact(id, "D", out var g) && g.ToString("D") == id;
    }
}
