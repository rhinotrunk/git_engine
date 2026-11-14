using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using RhinoTrunk.Contracts;

namespace RhinoTrunk.Client;

/// <summary>Thrown for every non-success response; carries the server's <see cref="ApiError"/>.</summary>
public sealed class RhinoTrunkApiException(HttpStatusCode status, ApiError error)
    : Exception($"{(int)status} {error.Code}: {error.Message}")
{
    public HttpStatusCode StatusCode { get; } = status;
    public ApiError Error { get; } = error;
}

/// <summary>
/// Typed client for the RhinoTrunk REST API (see docs/API.md). Dependency-free so it can load inside Rhino 8.
/// Revisions ("rev") accept a commit hash, a branch name or a tag name.
/// </summary>
public sealed class RhinoTrunkClient
{
    private readonly HttpClient _http;

    public RhinoTrunkClient(HttpClient http)
    {
        _http = http;
        // Relative paths only combine correctly when the base address ends with '/'.
        if (http.BaseAddress is { } b && !b.AbsoluteUri.EndsWith('/'))
            http.BaseAddress = new Uri(b.AbsoluteUri + "/");
    }

    public RhinoTrunkClient(Uri baseAddress) : this(new HttpClient { BaseAddress = baseAddress }) { }

    public HttpClient Http => _http;

    // ---------------------------------------------------------------- repositories

    public Task<RepoInfo> CreateRepoAsync(string name, CancellationToken ct = default) =>
        SendAsync<RepoInfo>(HttpMethod.Post, "repos", new CreateRepoRequest { Name = name }, ct);

    public Task<ReposResponse> ListReposAsync(CancellationToken ct = default) => SendAsync<ReposResponse>(HttpMethod.Get, "repos", null, ct);

    public Task<RepoInfo> GetRepoAsync(string repo, CancellationToken ct = default) => SendAsync<RepoInfo>(HttpMethod.Get, R(repo), null, ct);

    // ---------------------------------------------------------------- objects

    /// <summary>Uploads bytes (idempotent) and returns their content hash.</summary>
    public async Task<string> PutObjectAsync(string repo, byte[] data, CancellationToken ct = default)
    {
        var hash = ContentHash.Compute(data);
        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{R(repo)}/objects/{E(hash)}") { Content = content };
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return hash;
    }

    /// <summary>Uploads only the objects the server does not have yet. Returns the hashes in input order.</summary>
    public async Task<List<string>> UploadAsync(string repo, IReadOnlyList<byte[]> blobs, CancellationToken ct = default)
    {
        var hashes = blobs.Select(b => ContentHash.Compute(b)).ToList();
        var byHash = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var i = 0; i < blobs.Count; i++) byHash[hashes[i]] = blobs[i];
        foreach (var chunk in byHash.Keys.Chunk(1000))
        {
            var missing = await MissingAsync(repo, chunk, ct);
            foreach (var hash in missing) await PutObjectAsync(repo, byHash[hash], ct);
        }
        return hashes;
    }

    public async Task<byte[]> GetObjectAsync(string repo, string hash, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{R(repo)}/objects/{E(hash)}", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<List<string>> MissingAsync(string repo, IEnumerable<string> hashes, CancellationToken ct = default) =>
        (await SendAsync<MissingResponse>(HttpMethod.Post, $"{R(repo)}/objects/missing", new HashesRequest { Hashes = hashes.ToList() }, ct)).Missing;

    public async Task<Dictionary<string, byte[]>> BatchGetAsync(string repo, IEnumerable<string> hashes, CancellationToken ct = default)
    {
        var response = await SendAsync<BatchGetResponse>(HttpMethod.Post, $"{R(repo)}/objects/batch-get", new HashesRequest { Hashes = hashes.ToList() }, ct);
        return response.Objects.ToDictionary(p => p.Key, p => Convert.FromBase64String(p.Value), StringComparer.Ordinal);
    }

    /// <summary>Reads a canonical-JSON component (e.g. "attributes") as a JSON object.</summary>
    public async Task<JsonObject?> GetJsonObjectAsync(string repo, string hash, CancellationToken ct = default) =>
        JsonNode.Parse(await GetObjectAsync(repo, hash, ct)) as JsonObject;

    // ---------------------------------------------------------------- refs

    public Task<RefsResponse> ListRefsAsync(string repo, CancellationToken ct = default) => SendAsync<RefsResponse>(HttpMethod.Get, $"{R(repo)}/refs", null, ct);

    /// <summary>Returns null if the branch does not exist.</summary>
    public async Task<RefInfo?> GetBranchAsync(string repo, string name, CancellationToken ct = default)
    {
        try
        {
            return await SendAsync<RefInfo>(HttpMethod.Get, $"{R(repo)}/refs/branches/{RefPath(name)}", null, ct);
        }
        catch (RhinoTrunkApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Creates (expected = null) or moves a branch with compare-and-swap.</summary>
    public Task<RefInfo> SetBranchAsync(string repo, string name, string commit, string? expected, CancellationToken ct = default) =>
        SendAsync<RefInfo>(HttpMethod.Put, $"{R(repo)}/refs/branches/{RefPath(name)}", new RefUpdateRequest { Commit = commit, Expected = expected }, ct);

    public async Task DeleteBranchAsync(string repo, string name, string expected, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"{R(repo)}/refs/branches/{RefPath(name)}?expected={E(expected)}", ct);
        await EnsureSuccessAsync(response, ct);
    }

    public Task<RefInfo> CreateTagAsync(string repo, string name, string commit, CancellationToken ct = default) =>
        SendAsync<RefInfo>(HttpMethod.Put, $"{R(repo)}/refs/tags/{RefPath(name)}", new RefUpdateRequest { Commit = commit }, ct);

    // ---------------------------------------------------------------- commits and snapshots

    public Task<CommitResponse> CommitAsync(string repo, CommitRequest request, CancellationToken ct = default) =>
        SendAsync<CommitResponse>(HttpMethod.Post, $"{R(repo)}/commits", request, ct);

    public Task<CommitInfo> GetCommitAsync(string repo, string rev, CancellationToken ct = default) =>
        SendAsync<CommitInfo>(HttpMethod.Get, $"{R(repo)}/commits/{E(rev)}", null, ct);

    public Task<EntitiesResponse> ListEntitiesAsync(string repo, string rev, string? doc = null, string? kind = null, bool includeManifests = false, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (doc is not null) query.Add("doc=" + E(doc));
        if (kind is not null) query.Add("kind=" + E(kind));
        if (includeManifests) query.Add("manifests=true");
        var q = query.Count > 0 ? "?" + string.Join("&", query) : "";
        return SendAsync<EntitiesResponse>(HttpMethod.Get, $"{R(repo)}/commits/{E(rev)}/entities{q}", null, ct);
    }

    public Task<EntityVersion> GetEntityAsync(string repo, string rev, string kind, string id, string doc = Names.DefaultDoc, CancellationToken ct = default) =>
        SendAsync<EntityVersion>(HttpMethod.Get, $"{R(repo)}/commits/{E(rev)}/entities/{E(kind)}/{E(id)}?doc={E(doc)}", null, ct);

    /// <summary>Differences from <paramref name="from"/> (null = empty snapshot) to <paramref name="to"/>.</summary>
    public Task<DiffResponse> DiffAsync(string repo, string? from, string to, bool includeManifests = false, CancellationToken ct = default) =>
        SendAsync<DiffResponse>(HttpMethod.Get, $"{R(repo)}/diff?to={E(to)}" + (from is null ? "" : "&from=" + E(from)) + (includeManifests ? "&manifests=true" : ""), null, ct);

    public Task<MergeBaseResponse> MergeBaseAsync(string repo, string a, string b, CancellationToken ct = default) =>
        SendAsync<MergeBaseResponse>(HttpMethod.Get, $"{R(repo)}/merge-base?a={E(a)}&b={E(b)}", null, ct);

    public Task<GraphResponse> GraphAsync(string repo, int limit = 500, IEnumerable<string>? refs = null, CancellationToken ct = default)
    {
        var q = "?limit=" + limit + string.Concat((refs ?? []).Select(r => "&ref=" + E(r)));
        return SendAsync<GraphResponse>(HttpMethod.Get, $"{R(repo)}/graph{q}", null, ct);
    }

    public Task<HistoryResponse> HistoryAsync(string repo, string kind, string id, string rev = Names.DefaultBranch, string doc = Names.DefaultDoc, int limit = 100, CancellationToken ct = default) =>
        SendAsync<HistoryResponse>(HttpMethod.Get, $"{R(repo)}/history/{E(kind)}/{E(id)}?rev={E(rev)}&doc={E(doc)}&limit={limit}", null, ct);

    public Task<MergeResponse> MergeAsync(string repo, MergeRequest request, CancellationToken ct = default) =>
        SendAsync<MergeResponse>(HttpMethod.Post, $"{R(repo)}/merges", request, ct);

    /// <summary>Dry run of a merge: merged tree, conflicts, rule resolutions, approval needs and the diff to show.</summary>
    public Task<MergePreviewResponse> PreviewMergeAsync(string repo, MergePreviewRequest request, CancellationToken ct = default) =>
        SendAsync<MergePreviewResponse>(HttpMethod.Post, $"{R(repo)}/merges/preview", request, ct);

    // ---------------------------------------------------------------- branches, policy, approvals

    public Task<BranchesResponse> ListBranchesAsync(string repo, CancellationToken ct = default) =>
        SendAsync<BranchesResponse>(HttpMethod.Get, $"{R(repo)}/branches", null, ct);

    public Task<BranchInfo> GetBranchInfoAsync(string repo, string name, CancellationToken ct = default) =>
        SendAsync<BranchInfo>(HttpMethod.Get, $"{R(repo)}/branches/{RefPath(name)}", null, ct);

    public Task<BranchInfo> CreateBranchAsync(string repo, CreateBranchRequest request, CancellationToken ct = default) =>
        SendAsync<BranchInfo>(HttpMethod.Post, $"{R(repo)}/branches", request, ct);

    public Task<RepoPolicy> GetPolicyAsync(string repo, CancellationToken ct = default) =>
        SendAsync<RepoPolicy>(HttpMethod.Get, $"{R(repo)}/policy", null, ct);

    public Task<RepoPolicy> SetPolicyAsync(string repo, PolicyUpdateRequest request, CancellationToken ct = default) =>
        SendAsync<RepoPolicy>(HttpMethod.Put, $"{R(repo)}/policy", request, ct);

    public Task<ApprovalInfo> CreateApprovalAsync(string repo, ApprovalRequest request, CancellationToken ct = default) =>
        SendAsync<ApprovalInfo>(HttpMethod.Post, $"{R(repo)}/approvals", request, ct);

    public Task<ApprovalsResponse> ListApprovalsAsync(string repo, string? branch = null, string? theirs = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (branch is not null) query.Add("branch=" + E(branch));
        if (theirs is not null) query.Add("theirs=" + E(theirs));
        return SendAsync<ApprovalsResponse>(HttpMethod.Get, $"{R(repo)}/approvals" + (query.Count > 0 ? "?" + string.Join("&", query) : ""), null, ct);
    }

    // ---------------------------------------------------------------- annotations and derived data

    public Task<Annotation> AddAnnotationAsync(string repo, string rev, Annotation annotation, CancellationToken ct = default) =>
        SendAsync<Annotation>(HttpMethod.Post, $"{R(repo)}/commits/{E(rev)}/annotations", annotation, ct);

    public Task<AnnotationsResponse> ListAnnotationsAsync(string repo, string rev, CancellationToken ct = default) =>
        SendAsync<AnnotationsResponse>(HttpMethod.Get, $"{R(repo)}/commits/{E(rev)}/annotations", null, ct);

    public Task<DerivedRecord> SetDerivedAsync(string repo, string source, string recipe, string result, CancellationToken ct = default) =>
        SendAsync<DerivedRecord>(HttpMethod.Put, $"{R(repo)}/derived/{E(source)}/{E(recipe)}", new DerivedPutRequest { Result = result }, ct);

    /// <summary>Returns null if nothing is recorded for this source and recipe.</summary>
    public async Task<DerivedRecord?> GetDerivedAsync(string repo, string source, string recipe, CancellationToken ct = default)
    {
        try
        {
            return await SendAsync<DerivedRecord>(HttpMethod.Get, $"{R(repo)}/derived/{E(source)}/{E(recipe)}", null, ct);
        }
        catch (RhinoTrunkApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- plumbing

    private static string E(string value) => Uri.EscapeDataString(value);

    private static string R(string repo) => "repos/" + E(repo);

    private static string RefPath(string name) => string.Join("/", name.Split('/').Select(E));

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: RhinoTrunkJson.Options);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(RhinoTrunkJson.Options, ct))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        ApiError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ApiError>(RhinoTrunkJson.Options, ct);
        }
        catch (JsonException)
        {
        }
        throw new RhinoTrunkApiException(response.StatusCode, error ?? new ApiError { Code = "http_" + (int)response.StatusCode, Message = response.ReasonPhrase ?? "" });
    }
}

/// <summary>Small helpers to build commit changes.</summary>
public static class Changes
{
    public static string NewId() => Guid.NewGuid().ToString("D");

    public static EntityRef Ref(string kind, string id, string role) => new() { Kind = kind, Id = id, Role = role };

    public static Change Put(string kind, string id, ManifestInput manifest, string doc = Names.DefaultDoc) =>
        new() { Op = ChangeOps.Put, Doc = doc, Kind = kind, Id = id, Manifest = manifest };

    public static Change Restore(string kind, string id, string version, string doc = Names.DefaultDoc) =>
        new() { Op = ChangeOps.Put, Doc = doc, Kind = kind, Id = id, Version = version };

    public static Change Delete(string kind, string id, string doc = Names.DefaultDoc) =>
        new() { Op = ChangeOps.Delete, Doc = doc, Kind = kind, Id = id };
}
