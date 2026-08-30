using GrayMoon.App.Models.Api;
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

        return routes;
    }

    private static async Task<Results<Ok<List<WorkspaceFileDto>>, NotFound>> GetWorkspaceFiles(
        int workspaceId,
        IWorkspaceFileOperations operations,
        CancellationToken cancellationToken)
    {
        var files = await operations.ListAsync(workspaceId, cancellationToken);
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

    private static async Task<Results<Ok<AgentSearchFilesResponse>, BadRequest<ProblemDetails>, NotFound>> SearchWorkspaceFiles(
        int workspaceId,
        string? pattern,
        string? repositoryName,
        IWorkspaceFileOperations operations,
        CancellationToken cancellationToken)
    {
        var (found, agentConnected, data, error) = await operations.SearchAsync(workspaceId, pattern, repositoryName, cancellationToken);
        if (!found)
            return TypedResults.NotFound();
        if (!agentConnected || data == null)
            return TypedResults.BadRequest(new ProblemDetails { Title = error ?? "Search failed." });
        return TypedResults.Ok(data);
    }
}
