using GrayMoon.App.Api;
using GrayMoon.App.Data;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Api.Endpoints;

public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/sync", PostSync);
        routes.MapGet("/api/sync/queue", GetQueueStatus);
        return routes;
    }

    private static async Task<IResult> PostSync(
        SyncRequest? body,
        GitHubRepositoryRepository repoRepository,
        WorkspaceRepository workspaceRepository,
        AppDbContext dbContext,
        SyncBackgroundService syncQueue,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.Sync");
        logger.LogInformation("POST /api/sync called");
        if (body == null)
            return Results.BadRequest("Request body is required.");
        var repositoryId = body.RepositoryId;
        var workspaceId = body.WorkspaceId;
        if (repositoryId <= 0)
            return Results.BadRequest("repositoryId is required and must be greater than 0.");
        if (workspaceId <= 0)
            return Results.BadRequest("workspaceId is required and must be greater than 0.");

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return Results.NotFound("Workspace not found for the given workspaceId.");

        var repo = await repoRepository.GetByIdAsync(repositoryId);
        if (repo == null)
            return Results.NotFound("Repository not found for the given repositoryId.");

        var isInWorkspace = await dbContext.WorkspaceRepositories
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.GitHubRepositoryId == repo.GitHubRepositoryId);
        if (!isInWorkspace)
            return Results.NotFound("Repository is not in the given workspace.");

        logger.LogInformation("POST /api/sync accepted: repositoryId={RepositoryId}, workspaceId={WorkspaceId}", repositoryId, workspaceId);

        // Enqueue the sync request to be processed by background workers with controlled parallelism
        if (!syncQueue.EnqueueSync(repositoryId, workspaceId))
        {
            logger.LogWarning("Failed to enqueue sync request (queue service unavailable)");
            return Results.Problem("Sync service is unavailable", statusCode: 503);
        }

        return Results.Accepted();
    }

    private static IResult GetQueueStatus(SyncBackgroundService syncQueue)
    {
        var queueDepth = syncQueue.GetQueueDepth();
        return Results.Ok(new { queueDepth, message = $"{queueDepth} sync request(s) pending" });
    }
}
