using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Orchestration;

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
    WorkspaceRepositoryStateWriter stateWriter,
    IHubContext<WorkspaceSyncHub> hubContext,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<WorkspaceCommitSyncHandler> logger)
{
    public async Task CommitSyncAsync(
        int workspaceId,
        int repositoryId,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress,
        Action<int, string?> setRepositoryError,
        Action<string?> setPageError)
    {
        progress.Report("Synchronizing commits...");

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
                await SetSyncStatusErrorAsync(dbContext, workspaceId, repositoryId, cancellationToken);
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
                return;
            }

            var result = AgentResponseJson.DeserializeAgentResponse<CommitSyncResponse>(response.Data);
            await ApplyResultToDbAsync(dbContext, stateWriter, workspaceId, repositoryId, result, cancellationToken);
            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

            if (result != null && !string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                setRepositoryError(repositoryId, result.ErrorMessage);
            }
            else if (result is { MergeConflict: true })
            {
                setRepositoryError(repositoryId, "Merge conflict detected. Merge aborted.");
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
                var scopedStateWriter = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();

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
                await ApplyResultToDbAsync(scopedDbContext, scopedStateWriter, workspaceId, repositoryId, result, cancellationToken);
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

    private static async Task ApplyResultToDbAsync(
        AppDbContext db,
        WorkspaceRepositoryStateWriter stateWriter,
        int workspaceId,
        int repositoryId,
        CommitSyncResponse? result,
        CancellationToken ct)
    {
        if (result == null)
        {
            await SetSyncStatusErrorAsync(db, workspaceId, repositoryId, ct);
            return;
        }

        var statusWrite = result.Success && !result.MergeConflict ? SyncStatusWrite.InSync : SyncStatusWrite.Error;
        await stateWriter.ApplyAsync(workspaceId, repositoryId, BuildSnapshot(result), new RepositoryStateWriteOptions
        {
            SyncStatus = statusWrite
        }, ct);
    }

    /// <summary>
    /// A successful pull moves the branch, so the agent reports the whole count group (including the
    /// comparison against the default branch) in <see cref="CommitSyncResponse.State"/>. Older agents and
    /// the failure paths only report outgoing and incoming, so those are mapped as a count-only snapshot
    /// that leaves the rest of the row untouched.
    /// </summary>
    private static RepositoryStateSnapshot BuildSnapshot(CommitSyncResponse result)
    {
        if (result.State != null)
            return result.State;

        var usableBranch = result.Branch is { Length: > 0 } and not "-" ? result.Branch : null;
        return new RepositoryStateSnapshot
        {
            BranchName = usableBranch,
            GitVersion = result.Version,
            OutgoingCommits = result.OutgoingCommits,
            IncomingCommits = result.IncomingCommits,
            DefaultBranchBehind = result.DefaultBranchBehind,
            DefaultBranchAhead = result.DefaultBranchAhead,
            HasUpstream = result.HasUpstream,
            IdentityProbed = usableBranch != null,
            GitVersionProbed = result.Version is { Length: > 0 } and not "-",
            CommitCountsProbed = false,
            UpstreamProbed = false,
        };
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
