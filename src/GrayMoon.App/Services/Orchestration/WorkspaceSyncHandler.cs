using System.Collections.Concurrent;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application.Features;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Handles sync operations (git status, version, branch, commit counts) for workspace repositories.
/// Stateless; all state is provided via callbacks.
/// </summary>
public sealed class WorkspaceSyncHandler(
    ILogger<WorkspaceSyncHandler> logger,
    IServiceScopeFactory serviceScopeFactory,
    IOptions<WorkspaceOptions> workspaceOptions)
{
    private readonly int _maxConcurrent = Math.Max(1, workspaceOptions?.Value?.MaxParallelOperations ?? 16);


    public async Task<IReadOnlyDictionary<int, RepoGitVersionInfo>> RunSyncAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int>? repositoryIds,
        bool skipDependencyLevelPersistence,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress,
        Action<int, RepoGitVersionInfo> updateRepoGitInfo,
        Action<int, RepoSyncStatus> setRepoSyncStatus,
        Action? onAppSideComplete = null)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

        try
        {
            var results = await workspaceGitService.SyncAsync(
                workspaceId,
                contextId,
                onProgress: (completed, total, repoId, info) =>
                {
                    progress.Report($"Synchronized {completed} of {total}", completed, total);
                    var status = !string.IsNullOrWhiteSpace(info.ErrorMessage) || info.Version != "-" || info.Branch != "-"
                        ? RepoSyncStatus.InSync
                        : RepoSyncStatus.Error;
                    setRepoSyncStatus(repoId, status);
                    updateRepoGitInfo(repoId, info);
                },
                onAppSideComplete: onAppSideComplete,
                repositoryIds: repositoryIds,
                skipDependencyLevelPersistence: skipDependencyLevelPersistence,
                cancellationToken: cancellationToken);

            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error running workspace sync for WorkspaceId={WorkspaceId}", workspaceId);
            throw;
        }
    }

    /// <summary>
    /// Freshness + safety analysis for Return to Default. Shared by UX, REST, and bulk callers.
    /// </summary>
    public async Task<ReturnToDefaultPlan> AnalyzeReturnToDefaultAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryIds);

        var ids = repositoryIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new ReturnToDefaultPlan(
                workspaceId,
                Array.Empty<ReturnToDefaultRepositoryPlan>(),
                CanProceedAutomatically: true,
                RequiresConfirmation: false,
                HasBlockingRepositories: false,
                BlockingReasons: Array.Empty<string>(),
                PullRequestRefreshFailed: false,
                AnalysisFailed: false,
                AnalysisError: null);
        }

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var prService = scope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>();
        var query = scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>();
        var contextResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();
        var isSpecialWorkspace = (await contextResolver.GetRequiredAsync(contextId, workspaceId, cancellationToken)).IsSpecialWorkspace;
        if (!isSpecialWorkspace)
        {
            return ReturnToDefaultPlan.Failed(
                workspaceId,
                "Return to Default is Workspace-only. Stay on the Feature and update dependencies from default-branch versions after merges, or Remove Feature when finished.");
        }

        progress.Report(ids.Count == 1
            ? "Fetching latest branch state..."
            : $"Fetching latest branch state for {ids.Count} repositories...");

        var fetchDone = 0;
        var fetchFailed = false;
        using (var fetchSemaphore = new SemaphoreSlim(_maxConcurrent))
        {
            var fetchTasks = ids.Select(async repoId =>
            {
                await fetchSemaphore.WaitAsync(cancellationToken);
                try
                {
                    // Each concurrent fetch needs its own DbContext, so resolve a fresh WorkspaceGitService
                    // per repo rather than sharing the outer scope's instance across parallel tasks.
                    await using var repoScope = serviceScopeFactory.CreateAsyncScope();
                    var repoGit = repoScope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                    return await repoGit.RefreshBranchesForRepositoryAsync(repoId, workspaceId, contextId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Fetch failed during return-to-default analysis. WorkspaceId={WorkspaceId}, RepositoryId={RepositoryId}", workspaceId, repoId);
                    return false;
                }
                finally
                {
                    fetchSemaphore.Release();
                    var done = Interlocked.Increment(ref fetchDone);
                    if (ids.Count > 1)
                        progress.Report($"Fetched {done} of {ids.Count}...", done, ids.Count);
                }
            });

            var fetchResults = await Task.WhenAll(fetchTasks);
            fetchFailed = fetchResults.Any(success => !success);
        }

        if (fetchFailed)
            return ReturnToDefaultPlan.Failed(workspaceId, "Fetch failed. Return to default was aborted.");

        var prRefreshFailed = false;
        try
        {
            await prService.RefreshPullRequestsAsync(workspaceId, ids, force: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "PR refresh failed during return-to-default analysis. WorkspaceId={WorkspaceId}", workspaceId);
            prRefreshFailed = true;
        }

        var repoPlans = new List<ReturnToDefaultRepositoryPlan>(ids.Count);
        foreach (var repoId in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dto = await query.GetSnapshotAsync(workspaceId, repoId, contextId, isSpecialWorkspace, cancellationToken: cancellationToken);
            if (dto == null)
                return ReturnToDefaultPlan.Failed(workspaceId, "Repository state could not be read. Return to default was aborted.");

            repoPlans.Add(ClassifyRepository(dto));
        }

        return BuildPlan(workspaceId, repoPlans, prRefreshFailed);
    }

    /// <summary>
    /// Executes Return to Default with explicit options. Continues after per-repository failures.
    /// </summary>
    public async Task<OperationResult> ExecuteReturnToDefaultAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        ReturnToDefaultOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryIds);
        ArgumentNullException.ThrowIfNull(options);

        var ids = repositoryIds.Distinct().ToList();
        if (ids.Count == 0)
            return OperationResult.Ok();

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var query = scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>();
        var contextResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();
        var isSpecialWorkspace = (await contextResolver.GetRequiredAsync(contextId, workspaceId, cancellationToken)).IsSpecialWorkspace;
        if (!isSpecialWorkspace)
            return OperationResult.Fail(
                "Return to Default is Workspace-only. Stay on the Feature and update dependencies from default-branch versions after merges, or Remove Feature when finished.");

        progress.Report(ids.Count == 1
            ? "Returning to default branch..."
            : $"Returning {ids.Count} repositories to default branch...");

        // Snapshot reads stay sequential on one DbContext; agent work runs in parallel with per-repo scopes.
        var workItems = new List<(int RepoId, string BranchName, bool DeleteRemote, bool ClosePr, int? PrNumber)>(ids.Count);
        var repoErrors = new ConcurrentDictionary<int, string>();
        var skippedAlreadyOnDefault = 0;

        foreach (var repoId in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dto = await query.GetSnapshotAsync(workspaceId, repoId, contextId, isSpecialWorkspace, cancellationToken: cancellationToken);
            if (dto == null)
            {
                repoErrors[repoId] = "Repository state could not be read.";
                continue;
            }

            if (!string.IsNullOrWhiteSpace(dto.CheckedOutTag))
            {
                repoErrors[repoId] = "Repository is on a tag.";
                continue;
            }

            var needsSync = !string.IsNullOrWhiteSpace(dto.BranchName)
                && !string.Equals(dto.BranchName, dto.DefaultBranchName, StringComparison.Ordinal);
            if (!needsSync)
            {
                skippedAlreadyOnDefault++;
                continue;
            }

            workItems.Add((
                repoId,
                dto.BranchName!,
                options.DeleteRemoteBranch && dto.BranchHasUpstream == true,
                options.CloseOpenPullRequest && dto.PullRequestNumber is > 0 && IsOpenPullRequest(dto),
                dto.PullRequestNumber));
        }

        var completed = skippedAlreadyOnDefault;
        using (var semaphore = new SemaphoreSlim(_maxConcurrent, _maxConcurrent))
        {
            await Task.WhenAll(workItems.Select(async item =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    await using var repoScope = serviceScopeFactory.CreateAsyncScope();
                    var repoGit = repoScope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                    var prService = repoScope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>();

                    if (item.ClosePr && item.PrNumber is > 0)
                    {
                        try
                        {
                            await prService.ClosePullRequestAsync(workspaceId, item.RepoId, item.PrNumber.Value, cancellationToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogWarning(ex, "Failed to close PR {PrNumber} for repository {RepositoryId} before return to default", item.PrNumber, item.RepoId);
                        }
                    }

                    var (success, errMsg) = await repoGit.ReturnToDefaultDirectAsync(
                        workspaceId,
                        contextId,
                        item.RepoId,
                        item.BranchName,
                        deleteRemoteBranch: item.DeleteRemote,
                        allowForceDeleteLocalBranch: options.AllowForceDeleteLocalBranch,
                        cancellationToken);

                    if (!success)
                        repoErrors[item.RepoId] = errMsg ?? "Return to default failed.";
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completed);
                    if (ids.Count > 1)
                        progress.Report($"Returned {done} of {ids.Count} to default branch", done, ids.Count);
                }
            }));
        }

        // One workspace-wide recompute after the whole batch (avoids N concurrent recompute races).
        await git.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, contextId, cancellationToken);

        return repoErrors.Count == 0
            ? OperationResult.Ok()
            : OperationResult.Fail("Return to default failed for one or more repositories.", repoErrors);
    }

    /// <summary>
    /// Unattended Return to Default: analyze, require automatic eligibility, then execute with
    /// documented unattended options. Aborts the whole batch on the first blocker or failure.
    /// </summary>
    public async Task<UnattendedReturnToDefaultResult> ReturnToDefaultUnattendedAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryIds);

        var ids = repositoryIds.Distinct().ToList();
        if (ids.Count == 0)
            return new UnattendedReturnToDefaultResult(false, "No repositories to return to default.");

        try
        {
            var plan = await AnalyzeReturnToDefaultAsync(workspaceId, contextId, ids, progress, cancellationToken);

            if (plan.AnalysisFailed)
                return new UnattendedReturnToDefaultResult(false, plan.AnalysisError ?? "Return to default was aborted.");

            if (plan.PullRequestRefreshFailed)
                return new UnattendedReturnToDefaultResult(false, "Could not refresh pull request state. Return to default was aborted.");

            if (!plan.CanProceedAutomatically)
            {
                var reason = plan.BlockingReasons.FirstOrDefault()
                    ?? "Return to default is not safe. Return to default was aborted.";
                return new UnattendedReturnToDefaultResult(false, reason);
            }

            var actionable = plan.Repositories
                .Where(r => !r.IsAlreadyOnDefault && !r.IsOnTag)
                .ToList();

            if (actionable.Count == 0)
                return new UnattendedReturnToDefaultResult(true, null);

            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

            progress.Report(actionable.Count == 1
                ? "Returning to default branch..."
                : $"Returning {actionable.Count} repositories to default branch...");

            // Bounded parallel fan-out (each task on its own DI scope/DbContext), matching Execute/Analyze.
            // The "abort the whole batch on first failure" contract is preserved: every task still runs
            // (an in-flight agent operation cannot be safely aborted mid-flight), but the first failure found
            // once the batch completes is what gets reported, same as the sequential version reported the
            // first failure it hit.
            var returnDone = 0;
            string? firstError = null;
            using (var semaphore = new SemaphoreSlim(_maxConcurrent))
            {
                var tasks = actionable.Select(async repo =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        await using var repoScope = serviceScopeFactory.CreateAsyncScope();
                        var repoGit = repoScope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

                        // Unattended policy: delete remote when upstream exists; always allow force-delete local; never close PRs.
                        return await repoGit.ReturnToDefaultDirectAsync(
                            workspaceId,
                            contextId,
                            repo.RepositoryId,
                            repo.CurrentBranch!,
                            deleteRemoteBranch: repo.HasUpstream,
                            allowForceDeleteLocalBranch: true,
                            cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release();
                        var done = Interlocked.Increment(ref returnDone);
                        if (actionable.Count > 1)
                            progress.Report($"Returned {done} of {actionable.Count} to default branch", done, actionable.Count);
                    }
                });

                var results = await Task.WhenAll(tasks);
                foreach (var (success, errMsg) in results)
                {
                    if (!success)
                        firstError ??= errMsg ?? "Return to default failed. Return to default was aborted.";
                }
            }

            await git.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, contextId, cancellationToken);

            if (firstError != null)
                return new UnattendedReturnToDefaultResult(false, firstError);

            return new UnattendedReturnToDefaultResult(true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unattended return-to-default failed. WorkspaceId={WorkspaceId}", workspaceId);
            return new UnattendedReturnToDefaultResult(false, "Return to default failed. Return to default was aborted.");
        }
    }

    internal static ReturnToDefaultRepositoryPlan ClassifyRepository(WorkspaceRepositoryLinkListItemDto dto)
    {
        var isOnTag = !string.IsNullOrWhiteSpace(dto.CheckedOutTag);
        var isAlreadyOnDefault = !isOnTag
            && (string.IsNullOrWhiteSpace(dto.BranchName)
                || string.Equals(dto.BranchName, dto.DefaultBranchName, StringComparison.Ordinal));

        var ahead = dto.DefaultBranchAheadCommits ?? 0;
        var prMergedOrClosed = dto.PullRequestMergedAt.HasValue
            || string.Equals(dto.PullRequestState, "closed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dto.PullRequestState, "merged", StringComparison.OrdinalIgnoreCase);

        var prState = NormalizePullRequestState(dto);
        var hasUpstream = dto.BranchHasUpstream == true;

        if (isAlreadyOnDefault)
        {
            return new ReturnToDefaultRepositoryPlan(
                dto.RepositoryId,
                dto.RepositoryName,
                dto.BranchName,
                dto.DefaultBranchName,
                IsAlreadyOnDefault: true,
                IsOnTag: false,
                CommitsAheadOfDefault: ahead,
                HasUpstream: hasUpstream,
                PullRequestState: prState,
                PullRequestNumber: dto.PullRequestNumber,
                CanDiscardLocalBranchSafely: true,
                RemoteBranchCanBeDeleted: false,
                RequiresExplicitDiscardConfirmation: false,
                BlockingReason: null);
        }

        if (isOnTag)
        {
            return new ReturnToDefaultRepositoryPlan(
                dto.RepositoryId,
                dto.RepositoryName,
                dto.BranchName,
                dto.DefaultBranchName,
                IsAlreadyOnDefault: false,
                IsOnTag: true,
                CommitsAheadOfDefault: ahead,
                HasUpstream: hasUpstream,
                PullRequestState: prState,
                PullRequestNumber: dto.PullRequestNumber,
                CanDiscardLocalBranchSafely: false,
                RemoteBranchCanBeDeleted: false,
                RequiresExplicitDiscardConfirmation: false,
                BlockingReason: "Repository is on a tag. Return to default was aborted.");
        }

        var requiresExplicitDiscard = ahead > 0 && !prMergedOrClosed;
        var canDiscardSafely = ahead == 0 || prMergedOrClosed;

        return new ReturnToDefaultRepositoryPlan(
            dto.RepositoryId,
            dto.RepositoryName,
            dto.BranchName,
            dto.DefaultBranchName,
            IsAlreadyOnDefault: false,
            IsOnTag: false,
            CommitsAheadOfDefault: ahead,
            HasUpstream: hasUpstream,
            PullRequestState: prState,
            PullRequestNumber: dto.PullRequestNumber,
            CanDiscardLocalBranchSafely: canDiscardSafely,
            RemoteBranchCanBeDeleted: hasUpstream,
            RequiresExplicitDiscardConfirmation: requiresExplicitDiscard,
            BlockingReason: requiresExplicitDiscard
                ? "Return to default is not safe. Return to default was aborted."
                : null);
    }

    private static ReturnToDefaultPlan BuildPlan(
        int workspaceId,
        IReadOnlyList<ReturnToDefaultRepositoryPlan> repoPlans,
        bool prRefreshFailed)
    {
        var blockingReasons = new List<string>();
        var hasBlocking = false;
        var requiresConfirmation = false;
        var canProceedAutomatically = !prRefreshFailed;

        foreach (var repo in repoPlans)
        {
            if (repo.IsAlreadyOnDefault)
                continue;

            if (repo.IsOnTag)
            {
                hasBlocking = true;
                canProceedAutomatically = false;
                if (repo.BlockingReason != null)
                    blockingReasons.Add(repo.BlockingReason);
                continue;
            }

            if (repo.RequiresExplicitDiscardConfirmation)
            {
                hasBlocking = true;
                canProceedAutomatically = false;
                requiresConfirmation = true;
                if (repo.BlockingReason != null)
                    blockingReasons.Add(repo.BlockingReason);
                continue;
            }

            if (repo.RemoteBranchCanBeDeleted || repo.CommitsAheadOfDefault > 0)
                requiresConfirmation = true;
        }

        if (prRefreshFailed)
        {
            hasBlocking = true;
            blockingReasons.Insert(0, "Could not refresh pull request state. Return to default was aborted.");
        }

        return new ReturnToDefaultPlan(
            workspaceId,
            repoPlans,
            CanProceedAutomatically: canProceedAutomatically,
            RequiresConfirmation: requiresConfirmation,
            HasBlockingRepositories: hasBlocking,
            BlockingReasons: blockingReasons,
            PullRequestRefreshFailed: prRefreshFailed,
            AnalysisFailed: false,
            AnalysisError: null);
    }

    private static string? NormalizePullRequestState(WorkspaceRepositoryLinkListItemDto dto)
    {
        if (dto.PullRequestMergedAt.HasValue)
            return "merged";
        if (string.Equals(dto.PullRequestState, "closed", StringComparison.OrdinalIgnoreCase))
            return "closed";
        if (string.Equals(dto.PullRequestState, "merged", StringComparison.OrdinalIgnoreCase))
            return "merged";
        if (string.Equals(dto.PullRequestState, "open", StringComparison.OrdinalIgnoreCase)
            || dto.PullRequestNumber is > 0)
            return "open";
        return string.IsNullOrWhiteSpace(dto.PullRequestState) ? null : dto.PullRequestState;
    }

    private static bool IsOpenPullRequest(WorkspaceRepositoryLinkListItemDto dto)
        => string.Equals(NormalizePullRequestState(dto), "open", StringComparison.OrdinalIgnoreCase);
}
