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

public static class CommitSyncEndpoints
{
    public static IEndpointRouteBuilder MapCommitSyncEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/commitsync", PostCommitSync);
        return routes;
    }

    /// <summary>Only repositories that are linked to the given workspace (WorkspaceRepositories) are accepted; others return 404.</summary>
    private static async Task<IResult> PostCommitSync(
        CommitSyncRequest? body,
        IAgentBridge agentBridge,
        GitHubRepositoryRepository repoRepository,
        WorkspaceRepository workspaceRepository,
        AppDbContext dbContext,
        IHubContext<WorkspaceSyncHub> hubContext,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GrayMoon.App.Api.CommitSync");
        logger.LogInformation("POST /api/commitsync called");
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

        // Only sync repos that are linked to this workspace; reject others
        var isInWorkspace = await dbContext.WorkspaceRepositories
            .AnyAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repo.RepositoryId);
        if (!isInWorkspace)
        {
            logger.LogWarning("CommitSync rejected: repository {RepositoryId} is not linked to workspace {WorkspaceId}", repositoryId, workspaceId);
            return Results.NotFound("Repository is not in the given workspace.");
        }

        if (!agentBridge.IsAgentConnected)
        {
            logger.LogWarning("CommitSync rejected: agent not connected");
            return Results.Problem("Agent not connected. Start GrayMoon.Agent to sync repositories.", statusCode: 503);
        }

        logger.LogInformation("CommitSync requested. repositoryId={RepositoryId}, workspaceId={WorkspaceId}", repositoryId, workspaceId);

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
            var response = await agentBridge.SendCommandAsync("CommitSyncRepository", args, CancellationToken.None);

            // Agent transport failure (e.g. agent threw): return same JSON shape so client can show error under repo
            if (!response.Success)
            {
                var err = response.Error ?? "Commit sync failed.";
                logger.LogWarning("CommitSync failed for repository {RepositoryId}: {Error}", repositoryId, err);
                await SetWorkspaceRepositorySyncStatusErrorAsync(dbContext, workspaceId, repositoryId);
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
                return Results.Ok(new { success = false, mergeConflict = false, errorMessage = err });
            }

            // Parse response and update database
            var commitSyncResponse = AgentResponseJson.DeserializeAgentResponse<CommitSyncResponse>(response.Data);
            var wr = await dbContext.WorkspaceRepositories
                .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId);

            if (commitSyncResponse != null && wr != null)
            {
                wr.OutgoingCommits = commitSyncResponse.OutgoingCommits;
                wr.IncomingCommits = commitSyncResponse.IncomingCommits;
                if (commitSyncResponse.Version != null && commitSyncResponse.Version != "-")
                    wr.GitVersion = commitSyncResponse.Version;
                if (commitSyncResponse.Branch != null && commitSyncResponse.Branch != "-")
                    wr.BranchName = commitSyncResponse.Branch;
                wr.SyncStatus = (commitSyncResponse.Success && !commitSyncResponse.MergeConflict) ? RepoSyncStatus.InSync : RepoSyncStatus.Error;
                await dbContext.SaveChangesAsync();
            }

            if (commitSyncResponse != null && !commitSyncResponse.Success && wr == null)
                await SetWorkspaceRepositorySyncStatusErrorAsync(dbContext, workspaceId, repositoryId);

            if (wr != null || commitSyncResponse != null)
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);

            var mergeConflict = commitSyncResponse?.MergeConflict ?? false;
            var errorMessage = commitSyncResponse?.ErrorMessage;

            return Results.Ok(new {
                success = commitSyncResponse?.Success ?? false,
                mergeConflict,
                errorMessage
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing CommitSync for repository {RepositoryId}", repositoryId);
            var err = ex.Message;
            try
            {
                await SetWorkspaceRepositorySyncStatusErrorAsync(dbContext, workspaceId, repositoryId);
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
            }
            catch
            {
                // Ignore secondary errors
            }
            return Results.Ok(new { success = false, mergeConflict = false, errorMessage = err });
        }
    }

    private static async Task SetWorkspaceRepositorySyncStatusErrorAsync(AppDbContext dbContext, int workspaceId, int repositoryId)
    {
        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId);
        if (wr != null)
        {
            wr.SyncStatus = RepoSyncStatus.Error;
            await dbContext.SaveChangesAsync();
        }
    }
}
