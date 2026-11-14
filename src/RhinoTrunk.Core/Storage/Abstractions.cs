using RhinoTrunk.Contracts;

namespace RhinoTrunk.Core.Storage;

/// <summary>
/// Immutable, content-addressed objects: blobs, manifests, tree nodes, commits.
/// Callers guarantee that <c>hash == ContentHash.Compute(data)</c> before calling <see cref="PutAsync"/>.
/// </summary>
public interface IObjectStore
{
    Task<bool> ExistsAsync(string hash, CancellationToken ct = default);
    Task<byte[]?> GetAsync(string hash, CancellationToken ct = default);
    /// <returns>True if the object was newly stored, false if it already existed.</returns>
    Task<bool> PutAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken ct = default);
}

public sealed record RefLogEntry(string Ref, string? Old, string? New, string Actor, DateTimeOffset Time, string Reason);

/// <summary>Mutable pointers (branches, tags) with compare-and-swap updates.</summary>
public interface IRefStore
{
    /// <summary>Full ref names ("refs/heads/main", "refs/tags/v1") to commit hashes.</summary>
    Task<IReadOnlyDictionary<string, string>> ListAsync(CancellationToken ct = default);
    Task<string?> GetAsync(string fullName, CancellationToken ct = default);
    /// <summary>
    /// Sets <paramref name="fullName"/> to <paramref name="newValue"/> (null deletes) only if its current value
    /// equals <paramref name="expected"/> (null meaning "does not exist"). Appends to the reflog on success.
    /// </summary>
    Task<bool> CompareAndSwapAsync(string fullName, string? expected, string? newValue, RefLogEntry log, CancellationToken ct = default);
    Task<IReadOnlyList<RefLogEntry>> ReadLogAsync(CancellationToken ct = default);
}

/// <summary>Mutable annotations attached to commits (CI checks, reviews). Never part of a snapshot.</summary>
public interface IAnnotationStore
{
    Task AppendAsync(string commit, Annotation annotation, CancellationToken ct = default);
    Task<IReadOnlyList<Annotation>> ListAsync(string commit, CancellationToken ct = default);
}

/// <summary>Derived data keyed by content (source hash + recipe), e.g. preview meshes. Rebuildable.</summary>
public interface IDerivedStore
{
    Task SetAsync(DerivedRecord record, CancellationToken ct = default);
    Task<DerivedRecord?> GetAsync(string source, string recipe, CancellationToken ct = default);
}

/// <summary>
/// Small mutable JSON documents that are not part of any snapshot: branch metadata ("branch/&lt;name&gt;"),
/// the repository policy ("policy") and merge approvals ("approval/&lt;id&gt;"). Keys may contain '/'.
/// </summary>
public interface IMetaStore
{
    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);
    /// <summary>Null deletes.</summary>
    Task SetAsync(string key, byte[]? value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default);
}

public interface IRepositoryStore
{
    string Name { get; }
    DateTimeOffset Created { get; }
    IObjectStore Objects { get; }
    IRefStore Refs { get; }
    IAnnotationStore Annotations { get; }
    IDerivedStore Derived { get; }
    IMetaStore Meta { get; }
}

public interface IRepositoryProvider
{
    Task<IReadOnlyList<IRepositoryStore>> ListAsync(CancellationToken ct = default);
    Task<IRepositoryStore?> GetAsync(string name, CancellationToken ct = default);
    /// <returns>Null if a repository with this name already exists.</returns>
    Task<IRepositoryStore?> CreateAsync(string name, DateTimeOffset created, CancellationToken ct = default);
}
