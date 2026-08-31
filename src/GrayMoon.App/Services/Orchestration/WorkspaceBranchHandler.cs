using System.Collections.Concurrent;
using GrayMoon.App.Api.Endpoints;
using GrayMoon.App.Models.Api;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Handles branch-related operations: common branches, create branches, checkout, and sync-to-default.
/// Stateless; UI state is owned by the caller. Calls <see cref="IWorkspaceBranchOperations"/> in-process.
/// </summary>
public sealed class WorkspaceBranchHandler(
    IServiceScopeFactory serviceScopeFactory,
    IWorkspaceBranchOperations branchOperations,
    ILogger<WorkspaceBranchHandler> logger)
{
    private const int DefaultMaxParallelOperations = 16;

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
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        IProgress<OperationProgress>? progress = null,
        bool syncState = false,
        CancellationToken cancellationToken = default)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

        return await workspaceGitService.CreateBranchesAsync(
            workspaceId,
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
        int repositoryId,
        string newBranchName,
        string baseBranch,
        bool setUpstream,
        CancellationToken cancellationToken)
    {
        var create = await branchOperations.CreateBranchAsync(workspaceId, repositoryId, newBranchName, baseBranch, cancellationToken);
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
            var upstream = await branchOperations.SetUpstreamAsync(workspaceId, repositoryId, newBranchName, cancellationToken);
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
        int repositoryId,
        string branchName,
        CancellationToken cancellationToken)
        => CheckoutBranchAsync(workspaceId, repositoryId, branchName, isTag: false, cancellationToken);

    public async Task<(bool Success, string? ErrorMessage)> CheckoutBranchAsync(
        int workspaceId,
        int repositoryId,
        string branchName,
        bool isTag,
        CancellationToken cancellationToken)
    {
        var failureLabel = isTag ? "Failed to checkout tag." : "Failed to checkout branch.";
        var outcome = await branchOperations.CheckoutAsync(workspaceId, repositoryId, branchName, isTag, cancellationToken);

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

    public async Task<(bool Success, string? ErrorMessage)> SyncToDefaultSingleAsync(
        int workspaceId,
        int repositoryId,
        string? currentBranchName,
        bool deleteRemoteBranch,
        bool allowForceDeleteLocalBranch,
        CancellationToken cancellationToken)
    {
        var outcome = await branchOperations.SyncToDefaultAsync(
            workspaceId,
            repositoryId,
            currentBranchName,
            deleteRemoteBranch,
            allowForceDeleteLocalBranch,
            cancellationToken);

        if (outcome.IsSuccessStatus)
            return (true, null);

        var errMsg = outcome.ErrorText ?? $"Failed to sync to default branch: {outcome.StatusCode}";
        logger.LogError("SyncToDefault failed for repo {RepositoryId}: {StatusCode}, {Error}", repositoryId, outcome.StatusCode, outcome.ErrorText);
        return (false, errMsg);
    }

    public async Task<WorkspaceBranchBulkResult> FetchBranchesForWorkspaceAsync(
        int workspaceId,
        IReadOnlyCollection<int> repositoryIds,
        Action<int, int>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (repositoryIds.Count == 0)
            return WorkspaceBranchBulkResult.Empty;

        var total = repositoryIds.Count;
        var completed = 0;
        var errors = new ConcurrentDictionary<int, string>();
        using var semaphore = new SemaphoreSlim(DefaultMaxParallelOperations, DefaultMaxParallelOperations);

        var tasks = repositoryIds.Select(async repositoryId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await using var scope = serviceScopeFactory.CreateAsyncScope();
                var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
                var outcome = await ops.RefreshBranchesAsync(workspaceId, repositoryId, cancellationToken);
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

    public async Task<WorkspaceBranchBulkResult> CheckoutBranchForWorkspaceAsync(
        int workspaceId,
        IReadOnlyCollection<int> repositoryIds,
        string branchName,
        Action<int, int>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (repositoryIds.Count == 0)
            return WorkspaceBranchBulkResult.Empty;

        var total = repositoryIds.Count;
        var completed = 0;
        var errors = new ConcurrentDictionary<int, string>();
        using var semaphore = new SemaphoreSlim(DefaultMaxParallelOperations, DefaultMaxParallelOperations);

        var tasks = repositoryIds.Select(async repositoryId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await using var scope = serviceScopeFactory.CreateAsyncScope();
                var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
                var outcome = await ops.CheckoutAsync(workspaceId, repositoryId, branchName, isTag: false, cancellationToken);
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
        return new WorkspaceBranchBulkResult(total - failureCount, failureCount, new Dictionary<int, string>(errors));
    }

    private static BranchesResponse? TryReadRefreshBody(object? body)
        => body as BranchesResponse ?? AgentResponseJson.DeserializeAgentResponse<BranchesResponse>(body);
}

public sealed record WorkspaceBranchBulkResult(
    int SuccessCount,
    int FailureCount,
    IReadOnlyDictionary<int, string> ErrorsByRepositoryId)
{
    public static WorkspaceBranchBulkResult Empty { get; } = new(0, 0, new Dictionary<int, string>());
}

public sealed record UpdateBranchFromDefaultResult(
    bool Success,
    bool HasConflicts,
    IReadOnlyList<string> ConflictFiles,
    string? ErrorMessage);
