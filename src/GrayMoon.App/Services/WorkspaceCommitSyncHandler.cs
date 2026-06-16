using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services;

/// <summary>
/// Handles commit-sync (pull) operations for a workspace.
/// Calls the agent directly so CommandOutput streams to TerminalSinkContext when invoked inside a background job.
/// Stateless; all UI state is provided by the caller via callbacks.
/// </summary>
public sealed class WorkspaceCommitSyncHandler(
    IAgentBridge agentBridge,
    WorkspaceRepository workspaceRepository,
    GitHubRepositoryRepository repoRepository,
    WorkspaceService workspaceService,
    ConnectorHealthService connectorHealthService,
    AppDbContext dbContext,
    IHubContext<WorkspaceSyncHub> hubContext,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<WorkspaceCommitSyncHandler> logger)
{
    public async Task CommitSyncAsync(
        int workspaceId,
        int repositoryId,
        CancellationToken cancellationToken,
        Func<string, Task> setProgress,
        Action<int, string?> setRepositoryError,
        Action<string?> setPageError)
    {
        await setProgress("Synchronizing commits...");

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
        {
            setPageError("Workspace not found.");
            return;
        }

        var repo = await repoRepository.GetByIdAsync(repositoryId, cancellationToken);
        if (repo == null)
        {
            setRepositoryError(repositoryId, "Repository not found.");
            return;
        }

        if (!agentBridge.IsAgentConnected)
        {
            setPageError("Agent not connected. Start GrayMoon.Agent to sync repositories.");
            return;
        }

        try
        {
            await connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, cancellationToken);
            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
            var args = new
            {
                workspaceName = workspace.Name,
                repositoryId = repo.RepositoryId,
                repositoryName = repo.RepositoryName,
                bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
                workspaceId,
                workspaceRoot
            };

            var response = await agentBridge.SendCommandAsync("CommitSyncRepository", args, cancellationToken);

            if (!response.Success)
            {
                var err = response.Error ?? "Commit sync failed.";
                logger.LogWarning("CommitSync failed for repository {RepositoryId}: {Error}", repositoryId, err);
                setRepositoryError(repositoryId, err);
                setPageError(err);
                await SetSyncStatusErrorAsync(dbContext, workspaceId, repositoryId, cancellationToken);
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
                return;
            }

            var result = AgentResponseJson.DeserializeAgentResponse<CommitSyncResponse>(response.Data);
            await ApplyResultToDbAsync(dbContext, workspaceId, repositoryId, result, cancellationToken);
            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

            if (result != null && !string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                setRepositoryError(repositoryId, result.ErrorMessage);
                await setProgress(result.MergeConflict ? "Merge conflict detected. Merge aborted." : "Commit sync completed with errors.");
            }
            else if (result is { MergeConflict: true })
            {
                await setProgress("Merge conflict detected. Merge aborted.");
            }
            else
            {
                setRepositoryError(repositoryId, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error executing CommitSync for repository {RepositoryId}", repositoryId);
            setRepositoryError(repositoryId, ex.Message);
            setPageError(ex.Message);
        }
    }

    public async Task CommitSyncLevelAsync(
        int workspaceId,
        IReadOnlyList<int> repositoryIds,
        CancellationToken cancellationToken,
        Func<int, int, Task> reportProgress,
        Action<int, string?> setRepositoryError,
        Action<string?> setPageError)
    {
        if (repositoryIds.Count == 0)
            return;

        if (!agentBridge.IsAgentConnected)
        {
            setPageError("Agent not connected. Start GrayMoon.Agent to sync repositories.");
            return;
        }

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
        {
            setPageError("Workspace not found.");
            return;
        }

        var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var total = repositoryIds.Count;
        var completedCount = 0;

        var tasks = repositoryIds.Select(async repositoryId =>
        {
            try
            {
                // Per-repo scope: isolates AppDbContext and scoped services from concurrent tasks
                await using var scope = serviceScopeFactory.CreateAsyncScope();
                var scopedRepoRepository = scope.ServiceProvider.GetRequiredService<GitHubRepositoryRepository>();
                var scopedConnectorHealth = scope.ServiceProvider.GetRequiredService<ConnectorHealthService>();
                var scopedDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var repo = await scopedRepoRepository.GetByIdAsync(repositoryId, cancellationToken);
                if (repo == null)
                {
                    setRepositoryError(repositoryId, "Repository not found.");
                    return;
                }

                await scopedConnectorHealth.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, cancellationToken);

                var args = new
                {
                    workspaceName = workspace.Name,
                    repositoryId = repo.RepositoryId,
                    repositoryName = repo.RepositoryName,
                    bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
                    workspaceId,
                    workspaceRoot
                };

                var response = await agentBridge.SendCommandAsync("CommitSyncRepository", args, cancellationToken);

                if (!response.Success)
                {
                    var err = response.Error ?? "Commit sync failed.";
                    logger.LogError("CommitSync failed for repository {RepositoryId}: {Error}", repositoryId, err);
                    setRepositoryError(repositoryId, err);
                    await SetSyncStatusErrorAsync(scopedDbContext, workspaceId, repositoryId, cancellationToken);
                    await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
                    return;
                }

                var result = AgentResponseJson.DeserializeAgentResponse<CommitSyncResponse>(response.Data);
                await ApplyResultToDbAsync(scopedDbContext, workspaceId, repositoryId, result, cancellationToken);
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

                if (result != null && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                    setRepositoryError(repositoryId, result.ErrorMessage);
                else if (result is { MergeConflict: true })
                    setRepositoryError(repositoryId, "Merge conflict detected. Merge aborted.");
                else
                    setRepositoryError(repositoryId, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error syncing commits for repository {RepositoryId}", repositoryId);
                setRepositoryError(repositoryId, "Commit sync failed. The GrayMoon Agent may be offline.");
            }
            finally
            {
                var completed = Interlocked.Increment(ref completedCount);
                await reportProgress(completed, total);
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Caller handles reload on cancel.
        }
    }

    private static async Task ApplyResultToDbAsync(AppDbContext db, int workspaceId, int repositoryId, CommitSyncResponse? result, CancellationToken ct)
    {
        var wr = await db.WorkspaceRepositories
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId && w.RepositoryId == repositoryId, ct);
        if (wr == null)
            return;

        if (result != null)
        {
            wr.OutgoingCommits = result.OutgoingCommits;
            wr.IncomingCommits = result.IncomingCommits;
            if (result.Version != null && result.Version != "-")
                wr.GitVersion = result.Version;
            if (result.Branch != null && result.Branch != "-")
                wr.BranchName = result.Branch;
            wr.SyncStatus = (result.Success && !result.MergeConflict) ? RepoSyncStatus.InSync : RepoSyncStatus.Error;
        }
        else
        {
            wr.SyncStatus = RepoSyncStatus.Error;
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SetSyncStatusErrorAsync(AppDbContext db, int workspaceId, int repositoryId, CancellationToken ct)
    {
        var wr = await db.WorkspaceRepositories
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId && w.RepositoryId == repositoryId, ct);
        if (wr != null)
        {
            wr.SyncStatus = RepoSyncStatus.Error;
            await db.SaveChangesAsync(ct);
        }
    }
}
