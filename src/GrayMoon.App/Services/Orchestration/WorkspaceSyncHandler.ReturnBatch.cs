using System.Diagnostics;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Orchestration;

public sealed partial class WorkspaceSyncHandler
{
    private const string FetchFailedMessage = "Fetch failed. Return to default was aborted.";
    private const string PullRequestRefreshFailedMessage = "Could not refresh pull request state. Return to default was aborted.";
    private const string StateUnreadableMessage = "Repository state could not be read. Return to default was aborted.";
    private const string NotSafeMessage = "Return to default is not safe. Return to default was aborted.";
    private const string FeatureContextMessage =
        "Return to Default is Workspace-only. Stay on the Feature and update dependencies from default-branch versions after merges, or Remove Feature when finished.";
    private const string ReconciliationFailedMessage = "Refreshing the workspace failed.";
    private const string ReconciliationCancelledMessage = "Workspace refresh was cancelled.";

    private enum BranchFetchOutcome
    {
        Ok,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Per-repository unattended return. Shared branch fetch is parallel and the pull-request refresh runs
    /// once for the repositories that fetched. Eligible repositories then return in parallel. One workspace
    /// recompute and broadcast runs after every admitted mutation has settled.
    /// </summary>
    public async Task<UnattendedReturnToDefaultBatchResult> ReturnToDefaultUnattendedBatchAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryIds);

        var orderedIds = DistinctRepositoryIds(repositoryIds);
        if (orderedIds.Count == 0)
        {
            return new UnattendedReturnToDefaultBatchResult(
                Array.Empty<UnattendedReturnToDefaultRepositoryResult>(),
                ReconciliationAttempted: false,
                ReconciliationSucceeded: true,
                ReconciliationError: null);
        }

        var results = new UnattendedReturnToDefaultRepositoryResult?[orderedIds.Count];
        var settled = 0;
        var failed = 0;
        var progressGate = new object();

        void ReportSettled(int repositoryId, bool success, string? repositoryName)
        {
            var done = Interlocked.Increment(ref settled);
            var failedNow = success ? Volatile.Read(ref failed) : Interlocked.Increment(ref failed);
            var name = success ? null : DisplayName(repositoryId, repositoryName);
            var message = FormatBatchProgress(orderedIds.Count, done, failedNow, name);
            lock (progressGate)
            {
                if (orderedIds.Count <= 1)
                    progress.Report(message);
                else
                    progress.Report(message, done, orderedIds.Count);
            }
        }

        progress.Report(orderedIds.Count == 1
            ? "Returning to default branch..."
            : $"Returning {orderedIds.Count} repositories to default...");

        await using (var scope = serviceScopeFactory.CreateAsyncScope())
        {
            var contextResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();
            var isSpecialWorkspace = (await contextResolver.GetRequiredAsync(contextId, workspaceId, cancellationToken)).IsSpecialWorkspace;
            if (!isSpecialWorkspace)
            {
                for (var i = 0; i < orderedIds.Count; i++)
                {
                    results[i] = Failed(orderedIds[i], FeatureContextMessage, UnattendedReturnFailureStage.Eligibility, mutationAttempted: false, mutationCompleted: false);
                    ReportSettled(orderedIds[i], success: false, repositoryName: null);
                }

                return Finish(results, reconciliationAttempted: false, reconciliationSucceeded: true, reconciliationError: null);
            }
        }

        var fetchOutcomes = new BranchFetchOutcome[orderedIds.Count];
        Array.Fill(fetchOutcomes, BranchFetchOutcome.Cancelled);
        using var fetchSemaphore = new SemaphoreSlim(_maxConcurrent);
        var fetchTasks = new Task[orderedIds.Count];
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var index = i;
            var repositoryId = orderedIds[i];
            fetchTasks[i] = FetchOneAsync(index, repositoryId);
        }

        await Task.WhenAll(fetchTasks);

        if (cancellationToken.IsCancellationRequested)
        {
            for (var i = 0; i < orderedIds.Count; i++)
            {
                if (fetchOutcomes[i] == BranchFetchOutcome.Failed)
                    results[i] = Failed(orderedIds[i], FetchFailedMessage, UnattendedReturnFailureStage.Fetch, false, false);
                else
                    results[i] = Failed(orderedIds[i], UnattendedReturnToDefaultBatchResult.CancelledMessage, UnattendedReturnFailureStage.Cancelled, false, false);
                ReportSettled(orderedIds[i], success: false, repositoryName: null);
            }

            return Finish(results, false, true, null);
        }

        var pending = new List<int>();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            if (fetchOutcomes[i] == BranchFetchOutcome.Ok)
            {
                pending.Add(i);
                continue;
            }

            var cancelled = fetchOutcomes[i] == BranchFetchOutcome.Cancelled;
            results[i] = cancelled
                ? Failed(orderedIds[i], UnattendedReturnToDefaultBatchResult.CancelledMessage, UnattendedReturnFailureStage.Cancelled, false, false)
                : Failed(orderedIds[i], FetchFailedMessage, UnattendedReturnFailureStage.Fetch, false, false);
            ReportSettled(orderedIds[i], success: false, repositoryName: null);
        }

        if (pending.Count == 0)
            return Finish(results, false, true, null);

        try
        {
            await using var prScope = serviceScopeFactory.CreateAsyncScope();
            var prService = prScope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>();
            var fetchedIds = pending.Select(index => orderedIds[index]).ToList();
            await prService.RefreshPullRequestsAsync(workspaceId, fetchedIds, force: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            foreach (var index in pending)
            {
                results[index] = Failed(orderedIds[index], UnattendedReturnToDefaultBatchResult.CancelledMessage, UnattendedReturnFailureStage.Cancelled, false, false);
                ReportSettled(orderedIds[index], success: false, repositoryName: null);
            }

            return Finish(results, false, true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PR refresh failed during unattended return batch. WorkspaceId={WorkspaceId}", workspaceId);
            foreach (var index in pending)
            {
                results[index] = Failed(orderedIds[index], PullRequestRefreshFailedMessage, UnattendedReturnFailureStage.PullRequestRefresh, false, false);
                ReportSettled(orderedIds[index], success: false, repositoryName: null);
            }

            return Finish(results, false, true, null);
        }

        var eligible = new List<(int Index, ReturnToDefaultRepositoryPlan Plan)>();
        await using (var readScope = serviceScopeFactory.CreateAsyncScope())
        {
            var query = readScope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>();
            foreach (var index in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var repositoryId = orderedIds[index];
                var dto = await query.GetSnapshotAsync(workspaceId, repositoryId, contextId, isSpecialWorkspace: true, cancellationToken: cancellationToken);
                if (dto == null)
                {
                    results[index] = Failed(repositoryId, StateUnreadableMessage, UnattendedReturnFailureStage.Eligibility, false, false);
                    ReportSettled(repositoryId, success: false, repositoryName: null);
                    continue;
                }

                var plan = ClassifyRepository(dto);
                if (plan.IsAlreadyOnDefault)
                {
                    results[index] = Completed(repositoryId, mutationAttempted: false, mutationCompleted: false);
                    ReportSettled(repositoryId, success: true, repositoryName: plan.RepositoryName);
                    continue;
                }

                if (plan.BlockingReason != null || string.IsNullOrWhiteSpace(plan.CurrentBranch))
                {
                    results[index] = Failed(
                        repositoryId,
                        plan.BlockingReason ?? NotSafeMessage,
                        UnattendedReturnFailureStage.Eligibility,
                        mutationAttempted: false,
                        mutationCompleted: false);
                    ReportSettled(repositoryId, success: false, repositoryName: plan.RepositoryName);
                    continue;
                }

                eligible.Add((index, plan));
            }
        }

        var mutationAttempted = 0;
        var activeReturns = 0;
        var maxActive = new int[1];
        using var returnSemaphore = new SemaphoreSlim(_maxConcurrent);
        var executions = new List<Task>(eligible.Count);
        foreach (var item in eligible)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results[item.Index] = Failed(
                    item.Plan.RepositoryId,
                    UnattendedReturnToDefaultBatchResult.CancelledMessage,
                    UnattendedReturnFailureStage.Cancelled,
                    mutationAttempted: false,
                    mutationCompleted: false);
                ReportSettled(item.Plan.RepositoryId, success: false, item.Plan.RepositoryName);
                continue;
            }

            executions.Add(ReturnOneAsync(item.Index, item.Plan));
        }

        await Task.WhenAll(executions);

        var admittedMutations = Volatile.Read(ref mutationAttempted);
        var reconciliationAttempted = admittedMutations > 0;
        var reconciliationSucceeded = true;
        string? reconciliationError = null;
        if (reconciliationAttempted)
        {
            try
            {
                await using var recomputeScope = serviceScopeFactory.CreateAsyncScope();
                var git = recomputeScope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                await git.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, contextId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                reconciliationSucceeded = false;
                reconciliationError = ReconciliationCancelledMessage;
                logger.LogInformation(
                    "Workspace refresh was cancelled after unattended return batch. WorkspaceId={WorkspaceId} ContextId={ContextId}",
                    workspaceId, contextId.Value);
            }
            catch (Exception ex)
            {
                reconciliationSucceeded = false;
                reconciliationError = ReconciliationFailedMessage;
                logger.LogError(ex, "Workspace recompute failed after unattended return batch. WorkspaceId={WorkspaceId}", workspaceId);
            }
        }

        var failedCount = results.Count(result => result is { Completed: false });
        logger.LogInformation(
            "Unattended return batch finished WorkspaceId={WorkspaceId} ContextId={ContextId} RepositoryCount={RepositoryCount} MutationCount={MutationCount} FailedCount={FailedCount} MaxActive={MaxActive} ReconciliationAttempted={ReconciliationAttempted} Stage={Stage}",
            workspaceId, contextId.Value, orderedIds.Count, admittedMutations, failedCount, maxActive[0], reconciliationAttempted, "batch");

        return Finish(results, reconciliationAttempted, reconciliationSucceeded, reconciliationError);

        async Task FetchOneAsync(int index, int repositoryId)
        {
            var admitted = false;
            try
            {
                await fetchSemaphore.WaitAsync(cancellationToken);
                admitted = true;
                await using var repoScope = serviceScopeFactory.CreateAsyncScope();
                var repoGit = repoScope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                var ok = await repoGit.RefreshBranchesForRepositoryAsync(repositoryId, workspaceId, contextId, cancellationToken);
                fetchOutcomes[index] = ok ? BranchFetchOutcome.Ok : BranchFetchOutcome.Failed;
            }
            catch (OperationCanceledException)
            {
                fetchOutcomes[index] = BranchFetchOutcome.Cancelled;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fetch failed during unattended return batch. WorkspaceId={WorkspaceId}, RepositoryId={RepositoryId}", workspaceId, repositoryId);
                fetchOutcomes[index] = BranchFetchOutcome.Failed;
            }
            finally
            {
                if (admitted)
                    fetchSemaphore.Release();
            }
        }

        async Task ReturnOneAsync(int index, ReturnToDefaultRepositoryPlan plan)
        {
            var admitted = false;
            var started = Stopwatch.GetTimestamp();
            try
            {
                await returnSemaphore.WaitAsync(cancellationToken);
                admitted = true;
                Interlocked.Increment(ref mutationAttempted);
                var nowActive = Interlocked.Increment(ref activeReturns);
                int snapshot;
                do
                {
                    snapshot = Volatile.Read(ref maxActive[0]);
                    if (nowActive <= snapshot)
                        break;
                }
                while (Interlocked.CompareExchange(ref maxActive[0], nowActive, snapshot) != snapshot);

                await using var repoScope = serviceScopeFactory.CreateAsyncScope();
                var repoGit = repoScope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                var (success, errorMessage) = await repoGit.ReturnToDefaultDirectAsync(
                    workspaceId,
                    contextId,
                    plan.RepositoryId,
                    plan.CurrentBranch!,
                    deleteRemoteBranch: plan.HasUpstream,
                    allowForceDeleteLocalBranch: true,
                    cancellationToken);

                results[index] = success
                    ? Completed(plan.RepositoryId, mutationAttempted: true, mutationCompleted: true)
                    : Failed(plan.RepositoryId, errorMessage ?? "Return to default failed.", UnattendedReturnFailureStage.Execution, mutationAttempted: true, mutationCompleted: false);
            }
            catch (OperationCanceledException)
            {
                results[index] = Failed(
                    plan.RepositoryId,
                    UnattendedReturnToDefaultBatchResult.CancelledMessage,
                    UnattendedReturnFailureStage.Cancelled,
                    mutationAttempted: admitted,
                    mutationCompleted: false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unattended return failed. WorkspaceId={WorkspaceId}, RepositoryId={RepositoryId}", workspaceId, plan.RepositoryId);
                results[index] = Failed(plan.RepositoryId, ex.Message, UnattendedReturnFailureStage.Execution, mutationAttempted: admitted, mutationCompleted: false);
            }
            finally
            {
                if (admitted)
                {
                    Interlocked.Decrement(ref activeReturns);
                    returnSemaphore.Release();
                }

                var outcome = results[index];
                if (outcome != null)
                {
                    logger.LogDebug(
                        "Unattended return repository settled WorkspaceId={WorkspaceId} ContextId={ContextId} RepositoryId={RepositoryId} Stage={Stage} ElapsedMs={ElapsedMs} Completed={Completed} MutationCompleted={MutationCompleted}",
                        workspaceId, contextId.Value, plan.RepositoryId, outcome.FailureStage,
                        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, outcome.Completed, outcome.MutationCompleted);
                    ReportSettled(plan.RepositoryId, outcome.Completed, plan.RepositoryName);
                }
            }
        }
    }

    private static List<int> DistinctRepositoryIds(IReadOnlyList<int> repositoryIds)
    {
        var seen = new HashSet<int>();
        var ordered = new List<int>(repositoryIds.Count);
        foreach (var repositoryId in repositoryIds)
        {
            if (seen.Add(repositoryId))
                ordered.Add(repositoryId);
        }

        return ordered;
    }

    private static UnattendedReturnToDefaultRepositoryResult Completed(int repositoryId, bool mutationAttempted, bool mutationCompleted)
        => new(repositoryId, true, null, UnattendedReturnFailureStage.None, mutationAttempted, mutationCompleted);

    private static UnattendedReturnToDefaultRepositoryResult Failed(
        int repositoryId,
        string reason,
        UnattendedReturnFailureStage stage,
        bool mutationAttempted,
        bool mutationCompleted)
        => new(repositoryId, false, reason, stage, mutationAttempted, mutationCompleted);

    private static UnattendedReturnToDefaultBatchResult Finish(
        UnattendedReturnToDefaultRepositoryResult?[] results,
        bool reconciliationAttempted,
        bool reconciliationSucceeded,
        string? reconciliationError)
    {
        var concrete = new UnattendedReturnToDefaultRepositoryResult[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            concrete[i] = results[i] ?? throw new InvalidOperationException($"Missing unattended return result at index {i}.");
        }

        return new UnattendedReturnToDefaultBatchResult(concrete, reconciliationAttempted, reconciliationSucceeded, reconciliationError);
    }

    private static string DisplayName(int repositoryId, string? repositoryName)
        => string.IsNullOrWhiteSpace(repositoryName) ? $"Repository {repositoryId}" : repositoryName;

    private static string FormatBatchProgress(int total, int done, int failed, string? failedName)
    {
        if (total <= 1)
        {
            return failed > 0
                ? failedName is null ? "Return to default failed." : $"Return to default failed: {failedName}"
                : "Returned to default branch";
        }

        if (failed > 0)
        {
            var line = $"Returned {done} of {total} repositories to default ({failed} failed)";
            return failedName is null ? line + "." : $"{line}. {failedName} failed.";
        }

        return $"Returned {done} of {total} repositories to default";
    }
}
