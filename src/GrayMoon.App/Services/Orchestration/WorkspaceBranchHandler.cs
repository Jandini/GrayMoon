using System.Collections.Concurrent;
using GrayMoon.App.Api.Endpoints;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services.Features;
using GrayMoon.Application.Features;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Handles branch-related operations: common branches, create branches, checkout, and return-to-default.
/// Stateless; UI state is owned by the caller. Calls <see cref="IWorkspaceBranchOperations"/> in-process.
/// </summary>
public sealed class WorkspaceBranchHandler(
    IServiceScopeFactory serviceScopeFactory,
    IWorkspaceBranchOperations branchOperations,
    IWorkspaceBranchOccupancyService branchOccupancyService,
    IFeatureBranchGuard featureBranchGuard,
    IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<WorkspaceBranchHandler> logger)
{
    private int MaxParallelOperations => Math.Max(1, workspaceOptions.Value.MaxParallelOperations);

    public async Task<CommonBranchesApiResult?> GetCommonBranchesAsync(int workspaceId, CancellationToken cancellationToken)
    {
        var outcome = await branchOperations.GetCommonBranchesAsync(workspaceId, cancellationToken);
        if (!outcome.IsSuccessStatus)
        {
            logger.LogWarning("Could not load common branches: {StatusCode}, {Error}", outcome.StatusCode, outcome.ErrorText);
            return null;
        }

        return outcome.Body as CommonBranchesApiResult;
    }

    public async Task<IReadOnlyDictionary<int, string>> CreateBranchesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        IProgress<OperationProgress>? progress = null,
        bool syncState = false,
        CancellationToken cancellationToken = default)
        => (await CreateBranchesWithOutcomeAsync(
            workspaceId, contextId, newBranchName, baseBranch, repositoryIds, progress, syncState, cancellationToken)).ErrorsByRepositoryId;

    public async Task<BranchCreationOutcome> CreateBranchesWithOutcomeAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        IProgress<OperationProgress>? progress = null,
        bool syncState = false,
        CancellationToken cancellationToken = default)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

        return await workspaceGitService.CreateBranchesWithOutcomeAsync(
            workspaceId,
            contextId,
            newBranchName,
            baseBranch,
            onProgress: (completed, total) =>
                progress.Report($"Created {completed} of {total} branches", completed, total),
            repositoryIds: repositoryIds,
            syncState: syncState,
            cancellationToken: cancellationToken);
    }

    public async Task<(bool Success, string? Error)> CreateSingleBranchAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string newBranchName,
        string baseBranch,
        bool setUpstream,
        CancellationToken cancellationToken)
    {
        var create = await branchOperations.CreateBranchAsync(workspaceId, contextId, repositoryId, newBranchName, baseBranch, cancellationToken);
        if (!create.IsSuccessStatus)
        {
            logger.LogError("Create branch failed: {StatusCode}, {Error}", create.StatusCode, create.ErrorText);
            return (false, "Failed to create branch.");
        }

        var result = create.Body as CreateBranchApiResult;
        if (result is null || !result.Success)
            return (false, result?.Error ?? "Failed to create branch.");

        if (setUpstream)
        {
            var upstream = await branchOperations.SetUpstreamAsync(workspaceId, contextId, repositoryId, newBranchName, cancellationToken);
            if (!upstream.IsSuccessStatus)
            {
                logger.LogWarning("Set upstream failed: {StatusCode}, {Error}", upstream.StatusCode, upstream.ErrorText);
                return (true, "Branch created but failed to set upstream.");
            }

            var upstreamResult = upstream.Body as CreateBranchApiResult;
            if (upstreamResult != null && !upstreamResult.Success)
                return (true, upstreamResult.Error ?? "Branch created but failed to set upstream.");
        }

        return (true, null);
    }

    public Task<(bool Success, string? ErrorMessage)> CheckoutBranchAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string branchName,
        CancellationToken cancellationToken)
        => CheckoutBranchAsync(workspaceId, contextId, repositoryId, branchName, isTag: false, cancellationToken);

    public async Task<(bool Success, string? ErrorMessage)> CheckoutBranchAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string branchName,
        bool isTag,
        CancellationToken cancellationToken)
    {
        var failureLabel = isTag ? "Failed to checkout tag." : "Failed to checkout branch.";
        var outcome = await branchOperations.CheckoutAsync(workspaceId, contextId, repositoryId, branchName, isTag, cancellationToken);

        if (outcome.IsSuccessStatus)
        {
            var result = outcome.Body as CheckoutBranchApiResult;
            if (result != null && !result.Success)
                return (false, !string.IsNullOrWhiteSpace(result.ErrorMessage) ? result.ErrorMessage : failureLabel);

            return (true, null);
        }

        var message = outcome.ErrorText ?? $"{failureLabel.TrimEnd('.')}: {outcome.StatusCode}";
        logger.LogError("Checkout {Kind} failed: {StatusCode}, {Error}", isTag ? "tag" : "branch", outcome.StatusCode, outcome.ErrorText);
        return (false, message);
    }

    public async Task<(bool Success, string? ErrorMessage)> ReturnToDefaultSingleAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? currentBranchName,
        bool deleteRemoteBranch,
        bool allowForceDeleteLocalBranch,
        CancellationToken cancellationToken)
    {
        var outcome = await branchOperations.ReturnToDefaultAsync(
            workspaceId,
            contextId,
            repositoryId,
            currentBranchName,
            deleteRemoteBranch,
            allowForceDeleteLocalBranch,
            cancellationToken);

        if (outcome.IsSuccessStatus)
            return (true, null);

        var errMsg = outcome.ErrorText ?? $"Failed to return to default branch: {outcome.StatusCode}";
        logger.LogError("ReturnToDefault failed for repo {RepositoryId}: {StatusCode}, {Error}", repositoryId, outcome.StatusCode, outcome.ErrorText);
        return (false, errMsg);
    }

    public async Task<WorkspaceBranchBulkResult> FetchBranchesForWorkspaceAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyCollection<int> repositoryIds,
        Action<int, int>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (repositoryIds.Count == 0)
            return WorkspaceBranchBulkResult.Empty;

        var total = repositoryIds.Count;
        var completed = 0;
        var errors = new ConcurrentDictionary<int, string>();
        using var semaphore = new SemaphoreSlim(MaxParallelOperations, MaxParallelOperations);

        var tasks = repositoryIds.Select(async repositoryId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await using var scope = serviceScopeFactory.CreateAsyncScope();
                var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
                var outcome = await ops.RefreshBranchesAsync(workspaceId, contextId, repositoryId, cancellationToken);
                if (!outcome.IsSuccessStatus)
                {
                    errors[repositoryId] = outcome.ErrorText ?? $"Failed to fetch branches: {outcome.StatusCode}";
                    return;
                }

                var result = TryReadRefreshBody(outcome.Body);
                if (result?.Success == false)
                    errors[repositoryId] = result.ErrorMessage ?? "Failed to fetch branches.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fetch branches failed for repository {RepositoryId}", repositoryId);
                errors[repositoryId] = "Failed to fetch branches.";
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completed);
                reportProgress?.Invoke(done, total);
            }
        });

        await Task.WhenAll(tasks);
        var failureCount = errors.Count;
        return new WorkspaceBranchBulkResult(total - failureCount, failureCount, new Dictionary<int, string>(errors));
    }

    /// <summary>
    /// Checks out <paramref name="branchName"/> across every repo in <paramref name="workspaceRepositoryIdsByRepositoryId"/>.
    /// Before attempting checkout, asks <see cref="IWorkspaceBranchOccupancyService"/> whether that branch is held
    /// by a Feature or another worktree for each repo; a held repo is skipped (never attempted, so it cannot fail
    /// mid-run with git's "already checked out" error) and reported in <see cref="WorkspaceBranchBulkResult.ErrorsByRepositoryId"/>
    /// with a descriptive message. If the occupancy answer cannot be determined for a repo (Worker offline, old
    /// Worker, or any other error), that repo's checkout is attempted exactly as before this check existed, and
    /// <see cref="WorkspaceBranchBulkResult.OccupancyCheckDegraded"/> is set so the caller can show one "could not
    /// check worktrees" notice instead of silently proceeding.
    /// </summary>
    public async Task<WorkspaceBranchBulkResult> CheckoutBranchForWorkspaceAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyDictionary<int, int> workspaceRepositoryIdsByRepositoryId,
        string branchName,
        Action<int, int>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (workspaceRepositoryIdsByRepositoryId.Count == 0)
            return WorkspaceBranchBulkResult.Empty;

        // A Feature keeps every repository on its own branch; the header hides bulk actions there, so this is defence only.
        var refusal = await featureBranchGuard.CheckAsync(
            contextId, repositoryId: 0, FeatureBranchAction.CreateBranch, branchName, isTag: false, cancellationToken);
        if (refusal != null)
        {
            var refused = workspaceRepositoryIdsByRepositoryId.Keys.ToDictionary(id => id, _ => refusal);
            return new WorkspaceBranchBulkResult(0, refused.Count, refused);
        }

        var errors = new ConcurrentDictionary<int, string>();
        var occupancyCheckDegraded = false;

        // Each check is a worker round trip (git worktree list) plus its own short-lived DbContext, so the
        // checks fan out bounded by the same limit as the checkouts below instead of running one repo at a time.
        using var occupancySemaphore = new SemaphoreSlim(MaxParallelOperations, MaxParallelOperations);
        var checkoutAllowed = new ConcurrentDictionary<int, bool>();
        var occupancyTasks = workspaceRepositoryIdsByRepositoryId.Select(async pair =>
        {
            var (repositoryId, workspaceRepositoryId) = pair;
            await occupancySemaphore.WaitAsync(cancellationToken);
            try
            {
                var badges = await branchOccupancyService.GetBadgesForRepositoryAsync(
                    workspaceId, workspaceRepositoryId, contextId, cancellationToken);
                if (badges.TryGetValue(branchName, out var badge) && !badge.AllowCheckout)
                {
                    errors[repositoryId] = badge.Kind == BranchOccupancyKind.Feature
                        ? $"Skipped: branch is held by Feature '{badge.FeatureName}'."
                        : "Skipped: branch is checked out in another worktree.";
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Branch occupancy check failed for repository {RepositoryId}; attempting checkout anyway", repositoryId);
                occupancyCheckDegraded = true;
            }
            finally
            {
                occupancySemaphore.Release();
            }
            checkoutAllowed[repositoryId] = true;
        });
        await Task.WhenAll(occupancyTasks);

        // Keep the caller's repository order so progress and checkout start order stay deterministic.
        var repositoryIdsToCheckout = workspaceRepositoryIdsByRepositoryId.Keys.Where(checkoutAllowed.ContainsKey).ToList();

        var total = workspaceRepositoryIdsByRepositoryId.Count;
        var completed = total - repositoryIdsToCheckout.Count;
        reportProgress?.Invoke(completed, total);
        using var semaphore = new SemaphoreSlim(MaxParallelOperations, MaxParallelOperations);

        var tasks = repositoryIdsToCheckout.Select(async repositoryId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await using var scope = serviceScopeFactory.CreateAsyncScope();
                var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
                var outcome = await ops.CheckoutAsync(workspaceId, contextId, repositoryId, branchName, isTag: false, cancellationToken);
                if (!outcome.IsSuccessStatus)
                {
                    errors[repositoryId] = outcome.ErrorText ?? $"Failed to checkout branch: {outcome.StatusCode}";
                    return;
                }

                if (outcome.Body is CheckoutBranchApiResult { Success: false } failed)
                    errors[repositoryId] = failed.ErrorMessage ?? "Failed to checkout branch.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Checkout failed for repository {RepositoryId}", repositoryId);
                errors[repositoryId] = "Failed to checkout branch.";
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completed);
                reportProgress?.Invoke(done, total);
            }
        });

        await Task.WhenAll(tasks);
        var failureCount = errors.Count;
        return new WorkspaceBranchBulkResult(total - failureCount, failureCount, new Dictionary<int, string>(errors), occupancyCheckDegraded);
    }

    private static BranchesResponse? TryReadRefreshBody(object? body)
        => body as BranchesResponse ?? WorkerResponseJson.DeserializeWorkerResponse<BranchesResponse>(body);
}

public sealed record WorkspaceBranchBulkResult(
    int SuccessCount,
    int FailureCount,
    IReadOnlyDictionary<int, string> ErrorsByRepositoryId,
    bool OccupancyCheckDegraded = false)
{
    public static WorkspaceBranchBulkResult Empty { get; } = new(0, 0, new Dictionary<int, string>());
}

public sealed record UpdateBranchFromDefaultResult(
    bool Success,
    bool HasConflicts,
    IReadOnlyList<string> ConflictFiles,
    string? ErrorMessage);
