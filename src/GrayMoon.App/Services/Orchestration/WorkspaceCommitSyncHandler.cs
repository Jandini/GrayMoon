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
using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Handles commit-sync (pull) operations for a workspace.
/// Calls the worker directly so CommandOutput streams to TerminalSinkContext when invoked inside a background job.
/// Stateless; all UI state is provided by the caller via callbacks.
/// </summary>
/// <remarks>
/// A pull runs with hooks off, so no merge hook reports what it brought in. The Worker returns the projects with
/// the pull result, and each pull job finishes with one recompute of the context's dependency and version-file
/// state (<see cref="CompletePullAsync"/>), whether it pulled one repository or many.
/// </remarks>
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
    IWorkspaceCapabilitiesResolver capabilitiesResolver,
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
            var capabilities = (await capabilitiesResolver.GetAsync(workspaceId, cancellationToken)).ToRepositoryOperationCapabilities();
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
                pullOnly,
                capabilities
            };

            // Read before the command is sent: a hook snapshot the App received earlier is older than this pull's result.
            var startedAt = RepositoryStateFreshness.Now();
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
            await ApplyResultToDbAsync(stateWriter, contextId, workspaceId, repositoryId, result, startedAt, deferProjects: false, cancellationToken);
            if (pullOnly)
                await RecordReferenceRevisionAsync(workspaceId, contextId, repositoryId, result, cancellationToken);
            if (result != null)
                await CompletePullAsync(workspaceId, contextId, [], setPageError, cancellationToken);
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
        var capabilities = (await capabilitiesResolver.GetAsync(workspaceId, cancellationToken)).ToRepositoryOperationCapabilities();

        // The repositories are pulled at the same time. Each writes its own row as it finishes; their projects are
        // held back and written as one projection after all of them, followed by one recompute for the whole pull.
        var pulled = new System.Collections.Concurrent.ConcurrentBag<(int RepositoryId, RepositoryStateSnapshot Snapshot)>();
        var anyResult = 0;

        var tasks = repositoryIds.Select(async repositoryId =>
        {
            try
            {
                // Per-repo scope: isolates AppDbContext and scoped services from concurrent tasks
                await using var scope = serviceScopeFactory.CreateAsyncScope();
                var scopedRepoRepository = scope.ServiceProvider.GetRequiredService<GitHubRepositoryRepository>();
                var scopedConnectorHealth = scope.ServiceProvider.GetRequiredService<ConnectorHealthService>();
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
                    pullOnly = pullOnlyIds.Contains(repositoryId),
                    capabilities
                };

                // Read before the command is sent: a hook snapshot the App received earlier is older than this pull's result.
                var startedAt = RepositoryStateFreshness.Now();
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
                await ApplyResultToDbAsync(scopedStateWriter, contextId, workspaceId, repositoryId, result, startedAt, deferProjects: true, cancellationToken);
                if (result != null)
                {
                    Interlocked.Exchange(ref anyResult, 1);
                    if (result.State is { ProjectsProbed: true } state)
                        pulled.Add((repositoryId, state));
                }
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

        if (anyResult == 1 && !cancellationToken.IsCancellationRequested)
        {
            await CompletePullAsync(workspaceId, contextId, pulled.ToList(), setPageError, cancellationToken);
            await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
        }

        return eligibility.Skipped;
    }

    /// <summary>
    /// The end of a pull job: writes the projects that were held back (<paramref name="deferredProjects"/>, empty
    /// when the state write already stored them), then recomputes the context's dependency and version-file state
    /// once and tells the pages of that context to reload. Runs in a scope of its own, so it shares no DbContext
    /// with the per-repository writes. A failure here does not undo the pull: it is logged and reported on the
    /// page, and the next Sync recomputes the same state.
    /// </summary>
    private async Task CompletePullAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<(int RepositoryId, RepositoryStateSnapshot Snapshot)> deferredProjects,
        Action<string?> setPageError,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            if (deferredProjects.Count > 0)
            {
                await scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>()
                    .ApplyProjectsAsync(contextId, workspaceId, deferredProjects, cancellationToken);
            }

            var recomputeScope = scope.ServiceProvider.GetRequiredService<WorkspaceStateRecomputeScope>();
            // A hook sync of the same context may be recomputing right now: share the run instead of racing it.
            var coalescer = scope.ServiceProvider.GetService<SyncRecomputeCoalescer>();
            if (coalescer is null)
                await recomputeScope.RecomputeAsync(workspaceId, contextId, cancellationToken);
            else
                await coalescer.RunAsync($"{workspaceId}:{contextId.Value}", () => recomputeScope.RecomputeAsync(workspaceId, contextId, cancellationToken));

            await hubContext.Clients.All.SendAsync("ContextSynced", workspaceId, contextId.Value, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Dependency refresh after pull failed for workspace {WorkspaceId} context {ContextId}", workspaceId, contextId.Value);
            setPageError("The pull finished, but the dependency state could not be refreshed. Run Sync to refresh it.");
        }
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
        WorkspaceRepositoryStateWriter stateWriter,
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        int repositoryId,
        CommitSyncResponse? result,
        long startedAt,
        bool deferProjects,
        CancellationToken ct)
    {
        if (result == null)
        {
            await SetSyncStatusErrorAsync(stateWriter, contextId, workspaceId, repositoryId, ct);
            return;
        }

        var statusWrite = result.Success && !result.MergeConflict ? SyncStatusWrite.InSync : SyncStatusWrite.Error;
        // The Worker reports the state it left the repository in (CommitSyncResponse.State) whenever it read the
        // checkout. A failure before that (the folder is missing, no branch to pull) reports none: nothing was read,
        // so only the sync status is written.
        await stateWriter.ApplyAsync(contextId, workspaceId, repositoryId, result.State ?? new RepositoryStateSnapshot(), new RepositoryStateWriteOptions
        {
            SyncStatus = statusWrite,
            OperationStartedAt = startedAt,
            DeferProjects = deferProjects,
        }, ct);
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
