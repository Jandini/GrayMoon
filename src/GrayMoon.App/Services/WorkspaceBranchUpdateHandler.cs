using GrayMoon.App.Api.Endpoints;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services;

/// <summary>
/// Handles the "Update Branch from Default" operation.
/// Calls the agent directly so CommandOutput streams to TerminalSinkContext when invoked inside a background job.
/// Stateless; all UI state is provided by the caller via callbacks.
/// </summary>
public sealed class WorkspaceBranchUpdateHandler(
    IAgentBridge agentBridge,
    WorkspaceRepository workspaceRepository,
    GitHubRepositoryRepository repoRepository,
    WorkspaceService workspaceService,
    ConnectorHealthService connectorHealthService,
    AppDbContext dbContext,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<WorkspaceBranchUpdateHandler> logger)
{
    public async Task<UpdateBranchFromDefaultResult> UpdateBranchFromDefaultAsync(
        int workspaceId,
        int repositoryId,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return new UpdateBranchFromDefaultResult(false, false, Array.Empty<string>(), "Workspace not found.");

        var repo = await repoRepository.GetByIdAsync(repositoryId, cancellationToken);
        if (repo == null)
            return new UpdateBranchFromDefaultResult(false, false, Array.Empty<string>(), "Repository not found.");

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId && w.RepositoryId == repositoryId, cancellationToken);
        if (wr == null)
            return new UpdateBranchFromDefaultResult(false, false, Array.Empty<string>(), "Repository is not in the given workspace.");

        if (!agentBridge.IsAgentConnected)
            return new UpdateBranchFromDefaultResult(false, false, Array.Empty<string>(), "Agent not connected. Start GrayMoon.Agent and try again.");

        try
        {
            await connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, cancellationToken);

            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
            var defaultBranchName = await dbContext.RepositoryBranches
                .Where(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsDefault && !rb.IsTag)
                .Select(rb => rb.BranchName)
                .FirstOrDefaultAsync(cancellationToken) ?? "main";

            var args = new
            {
                workspaceName = workspace.Name,
                repositoryName = repo.RepositoryName,
                currentBranchName = wr.BranchName,
                defaultBranchName,
                bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
                workspaceRoot
            };

            // AgentBridge.SendCommandAsync reads TerminalSinkContext.Current (set by BackgroundJobService.StartJob)
            // so all git output streams to the loading overlay terminal automatically.
            var response = await agentBridge.SendCommandAsync("UpdateBranchFromDefault", args, cancellationToken);

            var updateResponse = AgentResponseJson.DeserializeAgentResponse<UpdateBranchFromDefaultResponse>(response.Data);
            var commandSuccess = updateResponse?.Success ?? response.Success;

            if (!commandSuccess && updateResponse?.HasConflicts != true)
            {
                var errorMessage = updateResponse?.ErrorMessage ?? response.Error ?? "Failed to update branch.";
                logger.LogWarning("UpdateBranchFromDefault failed for repository {RepositoryId}: {Error}", repositoryId, errorMessage);
                return new UpdateBranchFromDefaultResult(false, false, Array.Empty<string>(), errorMessage);
            }

            if (commandSuccess && updateResponse != null)
            {
                if (updateResponse.OutgoingCommits.HasValue)
                    wr.OutgoingCommits = updateResponse.OutgoingCommits.Value;
                if (updateResponse.IncomingCommits.HasValue)
                    wr.IncomingCommits = updateResponse.IncomingCommits.Value;
                if (updateResponse.DefaultBranchBehind.HasValue)
                    wr.DefaultBranchBehindCommits = updateResponse.DefaultBranchBehind.Value;
                if (updateResponse.DefaultBranchAhead.HasValue)
                    wr.DefaultBranchAheadCommits = updateResponse.DefaultBranchAhead.Value;
                await dbContext.SaveChangesAsync(cancellationToken);
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
            }

            return new UpdateBranchFromDefaultResult(
                commandSuccess,
                updateResponse?.HasConflicts ?? false,
                updateResponse?.ConflictFiles ?? Array.Empty<string>(),
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating branch from default for repository {RepositoryId}", repositoryId);
            return new UpdateBranchFromDefaultResult(false, false, Array.Empty<string>(), "An unexpected error occurred while updating branch.");
        }
    }
}
