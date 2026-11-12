using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services.Policy;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using GrayMoon.Application.Features;
using GrayMoon.Application.Repositories.Policy;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Handles commit-sync (pull) operations for a workspace.
/// Calls the worker directly so CommandOutput streams to TerminalSinkContext when invoked inside a background job.
/// Stateless; all UI state is provided by the caller via callbacks.
/// </summary>
public sealed class WorkspaceCommitSyncHandler(
    IWorkerBridge workerBridge,
    WorkspaceRepository workspaceRepository,
    GitHubRepositoryRepository repoRepository,
    ConnectorHealthService connectorHealthService,
    AppDbContext dbContext,
    WorkspaceRepositoryStateWriter stateWriter,
    IHubContext<WorkspaceSyncHub> hubContext,
    IServiceScopeFactory serviceScopeFactory,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceRepositoryOperationPolicy operationPolicy,
    ReferenceRevisionRecorder revisionRecorder,
    ILogger<WorkspaceCommitSyncHandler> logger)
{
    public async Task CommitSyncAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
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

        var decision = await operationPolicy.EvaluateAsync(
            workspaceId, contextId, RepositoryOperation.Pull, repositoryId, cancellationToken: cancellationToken);
        if (!decision.IsAllowed)
        {
            setRepositoryError(repositoryId, decision.Message);
            return;
        }

        if (!workerBridge.IsWorkerConnected)
        {
            setPageError("Worker not connected. Start the GrayMoon Worker to sync repositories.");
            return;
        }

        try
        {
            await connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, cancellationToken);
            var workerArgs = await pathResolver.GetWorkerArgsAsync(contextId, cancellationToken);
            var workspaceRoot = workerArgs.WorkspaceRoot;
            var workspaceFolderName = workerArgs.WorkspaceFolderName;
            var workspaceRepositoryName = workerArgs.WorkspaceRepositoryName;
            var divergenceBaseBranch = await (
                from r in dbContext.WorkspaceFeatureRepositories.AsNoTracking()
                join l in dbContext.WorkspaceRepositories.AsNoTracking()
                    on r.WorkspaceRepositoryId equals l.WorkspaceRepositoryId
                where r.WorkspaceFeatureContextId == contextId.Value && l.RepositoryId == repositoryId
                select r.ParentBranchName
            ).FirstOrDefaultAsync(cancellationToken);

            // A read-only repository only takes incoming commits by fast-forward and is never pushed.
            var subjects = await operationPolicy.GetSubjectsAsync(workspaceId, contextId, cancellationToken: cancellationToken);
            var pullOnly = subjects.TryGetValue(repositoryId, out var subject) && subject is { IsReference: true, IsWorkspaceRepository: false };
            var args = new
            {
                workspaceName = workspaceFolderName,
                repositoryId = repo.RepositoryId,
                repositoryName = repo.RepositoryName,
                bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
                workspaceId,
                workspaceRoot,
                workspaceRepositoryName,
                divergenceBaseBranch,
                pullOnly
            };

            var response = await workerBridge.SendCommandAsync("CommitSyncRepository", args, cancellationToken);

            if (!response.Success)
            {
                var err = response.Error ?? "Commit sync failed.";
                logger.LogWarning("CommitSync failed for repository {RepositoryId}: {Error}", repositoryId, err);
                setRepositoryError(repositoryId, err);
                await SetSyncStatusErrorAsync(stateWriter, contextId, workspaceId, repositoryId, cancellationToken);
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
                return;
            }

            var result = WorkerResponseJson.DeserializeWorkerResponse<CommitSyncResponse>(response.Data);
            await ApplyResultToDbAsync(dbContext, stateWriter, contextId, workspaceId, repositoryId, result, cancellationToken);
            if (pullOnly)
                await RecordReferenceRevisionAsync(workspaceId, contextId, repositoryId, result, cancellationToken);
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

    /// <summary>Pulls the given repositories. Returns the ones the policy left out; they are not sent to the Worker.</summary>
    public async Task<IReadOnlyList<RepositorySkip>> CommitSyncLevelAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        CancellationToken cancellationToken,
        Func<int, int, Task> reportProgress,
        Action<int, string?> setRepositoryError,
        Action<string?> setPageError)
    {
        if (repositoryIds.Count == 0)
            return [];

        if (!workerBridge.IsWorkerConnected)
        {
            setPageError("Worker not connected. Start the GrayMoon Worker to sync repositories.");
            return [];
        }

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
        {
            setPageError("Workspace not found.");
            return [];
        }

        var eligibility = await operationPolicy.PartitionAsync(
            workspaceId, contextId, RepositoryOperation.Pull, repositoryIds, cancellationToken: cancellationToken);
        var skippedIds = eligibility.Skipped.Select(skip => skip.RepositoryId).ToHashSet();
        if (skippedIds.Count > 0)
            repositoryIds = repositoryIds.Where(id => !skippedIds.Contains(id)).ToList();
        if (repositoryIds.Count == 0)
            return eligibility.Skipped;

        var workerArgs = await pathResolver.GetWorkerArgsAsync(contextId, cancellationToken);
        var workspaceRoot = workerArgs.WorkspaceRoot;
        var workspaceFolderName = workerArgs.WorkspaceFolderName;
        var workspaceRepositoryName = workerArgs.WorkspaceRepositoryName;
        var total = repositoryIds.Count;
        var completedCount = 0;

        var parentByRepoId = await (
            from r in dbContext.WorkspaceFeatureRepositories.AsNoTracking()
            join l in dbContext.WorkspaceRepositories.AsNoTracking()
                on r.WorkspaceRepositoryId equals l.WorkspaceRepositoryId
            where r.WorkspaceFeatureContextId == contextId.Value
            select new { l.RepositoryId, r.ParentBranchName }
        ).ToDictionaryAsync(x => x.RepositoryId, x => x.ParentBranchName, cancellationToken);

        // A read-only repository only takes incoming commits by fast-forward and is never pushed. Decided per
        // repository, so one bulk pull mixes both modes.
        var pullOnlyIds = (await operationPolicy.GetSubjectsAsync(workspaceId, contextId, cancellationToken: cancellationToken))
            .Where(pair => pair.Value is { IsReference: true, IsWorkspaceRepository: false })
            .Select(pair => pair.Key)
            .ToHashSet();

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

                parentByRepoId.TryGetValue(repositoryId, out var divergenceBaseBranch);
                var args = new
                {
                    workspaceName = workspaceFolderName,
                    repositoryId = repo.RepositoryId,
                    repositoryName = repo.RepositoryName,
                    bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
                    workspaceId,
                    workspaceRoot,
                    workspaceRepositoryName,
                    divergenceBaseBranch,
                    pullOnly = pullOnlyIds.Contains(repositoryId)
                };

                var response = await workerBridge.SendCommandAsync("CommitSyncRepository", args, cancellationToken);

                if (!response.Success)
                {
                    var err = response.Error ?? "Commit sync failed.";
                    logger.LogError("CommitSync failed for repository {RepositoryId}: {Error}", repositoryId, err);
                    setRepositoryError(repositoryId, err);
                    await SetSyncStatusErrorAsync(scopedStateWriter, contextId, workspaceId, repositoryId, cancellationToken);
                    await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
                    return;
                }

                var result = WorkerResponseJson.DeserializeWorkerResponse<CommitSyncResponse>(response.Data);
                await ApplyResultToDbAsync(scopedDbContext, scopedStateWriter, contextId, workspaceId, repositoryId, result, cancellationToken);
                if (pullOnlyIds.Contains(repositoryId))
                    await RecordReferenceRevisionAsync(workspaceId, contextId, repositoryId, result, cancellationToken);
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
                setRepositoryError(repositoryId, "Commit sync failed. The GrayMoon Worker may be offline.");
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

        return eligibility.Skipped;
    }

    /// <summary>
    /// After a pull of a read-only repository the Workspace definition records where it is now. The recorder
    /// leaves out a Feature and never fails the pull: a failed write is logged there.
    /// </summary>
    private async Task RecordReferenceRevisionAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        CommitSyncResponse? result,
        CancellationToken cancellationToken)
    {
        if (result is null || !string.IsNullOrWhiteSpace(result.ErrorMessage))
            return;

        var commit = result.State?.HeadCommit ?? result.Commit;
        var branch = result.State?.BranchName ?? result.Branch;
        await revisionRecorder.RecordAsync(workspaceId, contextId, repositoryId, branch, commit, cancellationToken);
    }

    private static async Task ApplyResultToDbAsync(
        AppDbContext db,
        WorkspaceRepositoryStateWriter stateWriter,
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        int repositoryId,
        CommitSyncResponse? result,
        CancellationToken ct)
    {
        if (result == null)
        {
            await SetSyncStatusErrorAsync(stateWriter, contextId, workspaceId, repositoryId, ct);
            return;
        }

        var statusWrite = result.Success && !result.MergeConflict ? SyncStatusWrite.InSync : SyncStatusWrite.Error;
        await stateWriter.ApplyAsync(contextId, workspaceId, repositoryId, BuildSnapshot(result), new RepositoryStateWriteOptions
        {
            SyncStatus = statusWrite
        }, ct);
    }

    /// <summary>
    /// A successful pull moves the branch, so the worker reports the whole count group (including the
    /// comparison against the default branch) in <see cref="CommitSyncResponse.State"/>. Older workers and
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

    /// <summary>
    /// Marks the repository as failed for the context the pull ran in. Goes through the state writer, so a Feature
    /// writes only its own context state and the special Workspace also mirrors onto the shared link. The empty
    /// snapshot probes nothing, so every other column stays as it is.
    /// </summary>
    private static Task SetSyncStatusErrorAsync(
        WorkspaceRepositoryStateWriter stateWriter,
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        int repositoryId,
        CancellationToken ct)
        => stateWriter.ApplyAsync(contextId, workspaceId, repositoryId, new RepositoryStateSnapshot(), new RepositoryStateWriteOptions
        {
            SyncStatus = SyncStatusWrite.Error
        }, ct);
}
