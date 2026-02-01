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
        return routes;
    }

    private static async Task<IResult> PostSync(
        SyncRequest? body,
        GitHubRepositoryRepository repoRepository,
        WorkspaceRepository workspaceRepository,
        AppDbContext dbContext,
        IServiceScopeFactory scopeFactory,
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
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                await svc.SyncSingleRepositoryAsync(repositoryId, workspaceId, default);
            }
            catch (Exception ex)
            {
                using var scope = scopeFactory.CreateScope();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<WorkspaceGitService>>();
                logger.LogError(ex, "Background sync failed for repository {RepositoryId} in workspace {WorkspaceId}", repositoryId, workspaceId);
            }
        });

        return Results.Accepted();
    }
}
