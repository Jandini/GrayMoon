using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Models.Api;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace GrayMoon.App.Api.Endpoints;

public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/workspaces/{workspaceId:int}").WithTags("Workspaces");

        group.MapGet("/files", GetWorkspaceFiles)
            .WithName("GetWorkspaceFiles");
        group.MapPost("/files", PostWorkspaceFiles)
            .WithName("PostWorkspaceFiles");
        group.MapGet("/files/search", SearchWorkspaceFiles)
            .WithName("SearchWorkspaceFiles");

        // For the Worker, on the one path that has no app request to read capabilities from: a git hook.
        // No /api prefix, matching GET /repos/{repoId}/connector, and gated by the same worker secret.
        routes.MapGet("/workspaces/{workspaceId:int}/capabilities", GetWorkspaceCapabilities);

        return routes;
    }

    internal static async Task<Results<Ok<RepositoryOperationCapabilities>, BadRequest<ProblemDetails>, NotFound>> GetWorkspaceCapabilities(
        int workspaceId,
        IWorkspaceCapabilitiesResolver resolver,
        CancellationToken cancellationToken)
    {
        if (workspaceId <= 0)
            return TypedResults.BadRequest(new ProblemDetails { Title = "workspaceId must be greater than 0." });

        WorkspaceCapabilities capabilities;
        try
        {
            capabilities = await resolver.GetAsync(workspaceId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return TypedResults.NotFound();
        }

        // Only the Worker-facing subset: what the Worker acts on, not the whole derived capability record.
        return TypedResults.Ok(capabilities.ToRepositoryOperationCapabilities());
    }

    private static async Task<Results<Ok<List<WorkspaceFileDto>>, NotFound>> GetWorkspaceFiles(
        int workspaceId,
        IWorkspaceFileOperations operations,
        IWorkspaceFeatureContextResolver contextResolver,
        CancellationToken cancellationToken)
    {
        var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var files = await operations.ListAsync(workspaceId, contextId, cancellationToken);
        return files == null ? TypedResults.NotFound() : TypedResults.Ok(files);
    }

    private static async Task<Results<Ok<object>, BadRequest<ProblemDetails>, NotFound>> PostWorkspaceFiles(
        int workspaceId,
        List<AddWorkspaceFileRequest>? body,
        IWorkspaceFileOperations operations,
        CancellationToken cancellationToken)
    {
        var (found, added) = await operations.AddAsync(workspaceId, body ?? [], cancellationToken);
        if (!found)
            return TypedResults.NotFound();
        return TypedResults.Ok<object>(new { added });
    }

    private static async Task<Results<Ok<WorkerSearchFilesResponse>, BadRequest<ProblemDetails>, NotFound>> SearchWorkspaceFiles(
        int workspaceId,
        string? pattern,
        string? repositoryName,
        IWorkspaceFileOperations operations,
        IWorkspaceFeatureContextResolver contextResolver,
        CancellationToken cancellationToken)
    {
        var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var (found, workerConnected, data, error) = await operations.SearchAsync(workspaceId, contextId, pattern, repositoryName, cancellationToken);
        if (!found)
            return TypedResults.NotFound();
        if (!workerConnected || data == null)
            return TypedResults.BadRequest(new ProblemDetails { Title = error ?? "Search failed." });
        return TypedResults.Ok(data);
    }
}
