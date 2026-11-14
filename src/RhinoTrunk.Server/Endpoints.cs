using Microsoft.AspNetCore.Mvc;
using RhinoTrunk.Contracts;
using RhinoTrunk.Core;

namespace RhinoTrunk.Server;

/// <summary>REST endpoints. Thin: every rule lives in <see cref="RhinoTrunkService"/>. See docs/API.md.</summary>
public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/", (EndpointDataSource source) => Results.Ok(new
        {
            service = "RhinoTrunk",
            version = typeof(Endpoints).Assembly.GetName().Version?.ToString(3),
            docs = "docs/API.md in the git_engine repository",
            endpoints = source.Endpoints.OfType<RouteEndpoint>()
                .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} {e.RoutePattern.RawText}")
                .OrderBy(s => s, StringComparer.Ordinal),
        }));
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // Repositories
        app.MapGet("/repos", (RhinoTrunkService svc, CancellationToken ct) => svc.ListReposAsync(ct));
        app.MapPost("/repos", async (CreateRepoRequest body, RhinoTrunkService svc, CancellationToken ct) =>
        {
            var repo = await svc.CreateRepoAsync(body.Name, ct);
            return Results.Created($"/repos/{repo.Name}", repo);
        });
        app.MapGet("/repos/{repo}", (string repo, RhinoTrunkService svc, CancellationToken ct) => svc.GetRepoAsync(repo, ct));

        var r = app.MapGroup("/repos/{repo}");

        // Objects (content-addressed blobs)
        r.MapPost("/objects/missing", (string repo, HashesRequest body, RhinoTrunkService svc, CancellationToken ct) => svc.MissingAsync(repo, body, ct));
        r.MapPost("/objects/batch-get", (string repo, HashesRequest body, RhinoTrunkService svc, CancellationToken ct) => svc.BatchGetAsync(repo, body, ct));
        r.MapPut("/objects/{hash}", async (string repo, string hash, HttpRequest request, RhinoTrunkService svc, CancellationToken ct) =>
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            var result = await svc.PutObjectAsync(repo, hash, buffer.ToArray(), ct);
            return result.Created ? Results.Created($"/repos/{repo}/objects/{hash}", result) : Results.Ok(result);
        });
        r.MapGet("/objects/{hash}", async (string repo, string hash, RhinoTrunkService svc, CancellationToken ct) =>
            Results.Bytes(await svc.GetObjectAsync(repo, hash, ct), "application/octet-stream"));

        // Refs
        r.MapGet("/refs", (string repo, RhinoTrunkService svc, CancellationToken ct) => svc.ListRefsAsync(repo, ct));
        MapRefs(r, "branches", RefTypes.Branch);
        MapRefs(r, "tags", RefTypes.Tag);

        // Commits and snapshots
        r.MapPost("/commits", async (string repo, CommitRequest body, RhinoTrunkService svc, CancellationToken ct) =>
        {
            var result = await svc.CommitAsync(repo, body, ct);
            return Results.Created($"/repos/{repo}/commits/{result.Commit}", result);
        });
        r.MapGet("/commits/{rev}", (string repo, string rev, RhinoTrunkService svc, CancellationToken ct) => svc.GetCommitAsync(repo, rev, ct));
        r.MapGet("/commits/{rev}/entities", (string repo, string rev, string? doc, string? kind, bool? manifests, RhinoTrunkService svc, CancellationToken ct) =>
            svc.ListEntitiesAsync(repo, rev, doc, kind, manifests ?? false, ct));
        r.MapGet("/commits/{rev}/entities/{kind}/{id}", (string repo, string rev, string kind, string id, string? doc, RhinoTrunkService svc, CancellationToken ct) =>
            svc.GetEntityAsync(repo, rev, doc ?? Names.DefaultDoc, kind, id, ct));
        r.MapGet("/commits/{rev}/annotations", (string repo, string rev, RhinoTrunkService svc, CancellationToken ct) => svc.ListAnnotationsAsync(repo, rev, ct));
        r.MapPost("/commits/{rev}/annotations", (string repo, string rev, Annotation body, RhinoTrunkService svc, CancellationToken ct) =>
            svc.AddAnnotationAsync(repo, rev, body, ct));

        r.MapGet("/diff", (string repo, string? from, string to, bool? manifests, RhinoTrunkService svc, CancellationToken ct) =>
            svc.DiffAsync(repo, from, to, manifests ?? false, ct));
        r.MapGet("/merge-base", (string repo, string a, string b, RhinoTrunkService svc, CancellationToken ct) => svc.MergeBaseAsync(repo, a, b, ct));
        r.MapGet("/graph", (string repo, [FromQuery(Name = "ref")] string[]? refs, int? limit, RhinoTrunkService svc, CancellationToken ct) =>
            svc.GraphAsync(repo, refs, limit ?? 500, ct));
        r.MapGet("/history/{kind}/{id}", (string repo, string kind, string id, string? rev, string? doc, int? limit, RhinoTrunkService svc, CancellationToken ct) =>
            svc.HistoryAsync(repo, rev ?? Names.DefaultBranch, doc ?? Names.DefaultDoc, kind, id, limit ?? 100, ct));
        r.MapPost("/merges", (string repo, MergeRequest body, RhinoTrunkService svc, CancellationToken ct) => svc.MergeAsync(repo, body, ct));
        r.MapPost("/merges/preview", (string repo, MergePreviewRequest body, RhinoTrunkService svc, CancellationToken ct) => svc.PreviewMergeAsync(repo, body, ct));

        // Branches with metadata, policy, approvals
        r.MapGet("/branches", (string repo, RhinoTrunkService svc, CancellationToken ct) => svc.ListBranchesAsync(repo, ct));
        r.MapGet("/branches/{**name}", (string repo, string name, RhinoTrunkService svc, CancellationToken ct) => svc.GetBranchInfoAsync(repo, name, ct));
        r.MapPost("/branches", async (string repo, CreateBranchRequest body, RhinoTrunkService svc, CancellationToken ct) =>
        {
            var branch = await svc.CreateBranchAsync(repo, body, ct);
            return Results.Created($"/repos/{repo}/branches/{branch.Name}", branch);
        });
        r.MapGet("/policy", (string repo, RhinoTrunkService svc, CancellationToken ct) => svc.GetPolicyAsync(repo, ct));
        r.MapPut("/policy", (string repo, PolicyUpdateRequest body, RhinoTrunkService svc, CancellationToken ct) => svc.SetPolicyAsync(repo, body, ct));
        r.MapGet("/approvals", (string repo, string? branch, string? theirs, RhinoTrunkService svc, CancellationToken ct) =>
            svc.ListApprovalsAsync(repo, branch, theirs, ct));
        r.MapGet("/approvals/{id}", (string repo, string id, RhinoTrunkService svc, CancellationToken ct) => svc.GetApprovalAsync(repo, id, ct));
        r.MapPost("/approvals", async (string repo, ApprovalRequest body, RhinoTrunkService svc, CancellationToken ct) =>
        {
            var approval = await svc.CreateApprovalAsync(repo, body, ct);
            return Results.Created($"/repos/{repo}/approvals/{approval.Id}", approval);
        });

        // Derived data (previews etc.), keyed by content
        r.MapGet("/derived/{source}/{recipe}", (string repo, string source, string recipe, RhinoTrunkService svc, CancellationToken ct) =>
            svc.GetDerivedAsync(repo, source, recipe, ct));
        r.MapPut("/derived/{source}/{recipe}", (string repo, string source, string recipe, DerivedPutRequest body, RhinoTrunkService svc, CancellationToken ct) =>
            svc.SetDerivedAsync(repo, source, recipe, body, ct));
    }

    private static void MapRefs(RouteGroupBuilder r, string segment, string type)
    {
        r.MapGet($"/refs/{segment}/{{**name}}", (string repo, string name, RhinoTrunkService svc, CancellationToken ct) => svc.GetRefAsync(repo, type, name, ct));
        r.MapPut($"/refs/{segment}/{{**name}}", (string repo, string name, RefUpdateRequest body, RhinoTrunkService svc, CancellationToken ct) =>
            svc.UpdateRefAsync(repo, type, name, body, "api", ct));
        r.MapDelete($"/refs/{segment}/{{**name}}", async (string repo, string name, string? expected, RhinoTrunkService svc, CancellationToken ct) =>
        {
            await svc.DeleteRefAsync(repo, type, name, expected, "api", ct);
            return Results.NoContent();
        });
    }
}
