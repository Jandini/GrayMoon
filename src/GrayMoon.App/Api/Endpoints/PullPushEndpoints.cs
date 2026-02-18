using GrayMoon.App.Api;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Api.Endpoints;

public static class PullPushEndpoints
{
    public static IEndpointRouteBuilder MapPullPushEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/pullpush", PostPullPush);
        return routes;
    }

    /// <summary>Only repositories that are linked to the given workspace (WorkspaceRepositories) are accepted; others return 404.</summary>
    private static async Task<IResult> PostPullPush(
        PullPushRequest? body,
        IAgentBridge agentBridge,
        GitHubRepositoryRepository repoRepository,
        WorkspaceRepository workspaceRepository,
        AppDbContext dbContext,
        IHubContext<WorkspaceSyncHub> hubContext,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.PullPush");
        logger.LogInformation("POST /api/pullpush called");
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

        // Only pull/push repos that are linked to this workspace; reject others
        var isInWorkspace = await dbContext.WorkspaceRepositories
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repo.RepositoryId);
        if (!isInWorkspace)
        {
            logger.LogWarning("PullPush rejected: repository {RepositoryId} is not linked to workspace {WorkspaceId}", repositoryId, workspaceId);
            return Results.NotFound("Repository is not in the given workspace.");
        }

        if (!agentBridge.IsAgentConnected)
        {
            logger.LogWarning("PullPush rejected: agent not connected");
            return Results.Problem("Agent not connected. Start GrayMoon.Agent to pull/push repositories.", statusCode: 503);
        }

        logger.LogInformation("PullPush requested. repositoryId={RepositoryId}, workspaceId={WorkspaceId}", repositoryId, workspaceId);

        try
        {
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryId = repo.RepositoryId,
                repositoryName = repo.RepositoryName,
                bearerToken = repo.Connector?.UserToken,
                workspaceId
            };
            var response = await agentBridge.SendCommandAsync("PullPushRepository", args, CancellationToken.None);
            
            if (!response.Success)
            {
                logger.LogWarning("PullPush failed for repository {RepositoryId}: {Error}", repositoryId, response.Error);
                return Results.Problem(response.Error ?? "PullPush failed", statusCode: 500);
            }

            // Parse response and update database
            var pullPushResponse = AgentResponseJson.DeserializeAgentResponse<PullPushResponse>(response.Data);
            if (pullPushResponse != null)
            {
                
                // Update workspace repository link with new commit counts
                var wr = await dbContext.WorkspaceRepositories
                    .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId);
                
                if (wr != null)
                {
                    wr.OutgoingCommits = pullPushResponse.OutgoingCommits;
                    wr.IncomingCommits = pullPushResponse.IncomingCommits;
                    if (pullPushResponse.Version != null && pullPushResponse.Version != "-")
                        wr.GitVersion = pullPushResponse.Version;
                    if (pullPushResponse.Branch != null && pullPushResponse.Branch != "-")
                        wr.BranchName = pullPushResponse.Branch;
                    wr.SyncStatus = pullPushResponse.MergeConflict ? RepoSyncStatus.Error : RepoSyncStatus.InSync;
                    await dbContext.SaveChangesAsync();
                }

                // Broadcast update to refresh UI
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
            }

            var mergeConflict = pullPushResponse?.MergeConflict ?? false;
            var errorMessage = pullPushResponse?.ErrorMessage;
            
            return Results.Ok(new { 
                success = pullPushResponse?.Success ?? false, 
                mergeConflict,
                errorMessage
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing PullPush for repository {RepositoryId}", repositoryId);
            return Results.Problem("An error occurred while executing PullPush", statusCode: 500);
        }
    }
}
