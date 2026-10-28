using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>Reported once per repository as a workspace scan progresses.</summary>
public sealed record GitChangesWorkspaceScanProgress(string RepositoryName, bool Success, int Completed, int Total);

/// <summary>
/// The single Git Changes status-scan routine for one workspace, shared by the periodic background
/// sweep, the on-open warm-up scan, the manual Refresh button, and silent +/- fill-in. Calls
/// <c>GetGitChangeStatus</c> for every repository in the workspace (or one repository) with bounded
/// parallelism, and pushes each successful result through the single-reader write queue.
/// Line-stats and immediate persists wait on that queue so the caller still observes the write.
/// </summary>
public interface IGitChangesWorkspaceScanner
{
    Task ScanWorkspaceAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken,
        Action<GitChangesWorkspaceScanProgress>? onProgress = null,
        bool includeLineStats = false,
        int? repositoryId = null,
        bool persistImmediately = false);

    /// <summary>
    /// Scans the given repositories (by Repository id) in one call, bounded by the scanner's own
    /// parallelism limit. The default implementation falls back to one sequential call per id.
    /// </summary>
    async Task ScanRepositoriesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyCollection<int> repositoryIds,
        CancellationToken cancellationToken,
        bool persistImmediately = false)
    {
        foreach (var repositoryId in repositoryIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScanWorkspaceAsync(
                workspaceId,
                contextId,
                cancellationToken,
                repositoryId: repositoryId,
                persistImmediately: persistImmediately);
        }
    }
}

public sealed class GitChangesWorkspaceScanner(
    IServiceScopeFactory scopeFactory,
    IOptions<GitChangesOptions> gitChangesOptions,
    ILogger<GitChangesWorkspaceScanner> logger) : IGitChangesWorkspaceScanner
{
    public Task ScanWorkspaceAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken,
        Action<GitChangesWorkspaceScanProgress>? onProgress = null,
        bool includeLineStats = false,
        int? repositoryId = null,
        bool persistImmediately = false)
        => ScanCoreAsync(
            workspaceId,
            contextId,
            repositoryId.HasValue ? [repositoryId.Value] : null,
            cancellationToken,
            onProgress,
            includeLineStats,
            persistImmediately);

    public Task ScanRepositoriesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyCollection<int> repositoryIds,
        CancellationToken cancellationToken,
        bool persistImmediately = false)
        => ScanCoreAsync(
            workspaceId,
            contextId,
            repositoryIds,
            cancellationToken,
            onProgress: null,
            includeLineStats: false,
            persistImmediately);

    private async Task ScanCoreAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyCollection<int>? repositoryIds,
        CancellationToken cancellationToken,
        Action<GitChangesWorkspaceScanProgress>? onProgress,
        bool includeLineStats,
        bool persistImmediately)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var workerBridge = scope.ServiceProvider.GetRequiredService<IWorkerBridge>();
        if (!workerBridge.IsWorkerConnected)
        {
            return;
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var workerClient = scope.ServiceProvider.GetRequiredService<IGitChangesWorkerClient>();
        var writeQueue = scope.ServiceProvider.GetRequiredService<WorkspaceGitChangesWriteQueue>();

        var targets = await GitChangesMonitorTargets.ResolveAsync(
            dbContext, pathResolver, workspaceId, contextId, repositoryIds, cancellationToken);

        if (targets.Count == 0)
        {
            return;
        }

        var completed = 0;
        var total = targets.Count;
        var scanStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var failed = 0;
        using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(gitChangesOptions.Value.MaxParallelRepositoryOperations, total)));

        var tasks = targets.Select(async target =>
        {
            await semaphore.WaitAsync(cancellationToken);
            var success = false;
            try
            {
                var result = await workerClient.GetStatusAsync(
                    target.Root, target.WorkspaceName, target.RepositoryName, target.WorkspaceRepositoryName,
                    target.WorkspaceId, target.RepositoryId, cancellationToken,
                    includeLineStats);

                if (result.Success && result.Snapshot != null)
                {
                    success = true;
                    var notification = new GitChangesSnapshotNotification
                    {
                        WorkspaceId = target.WorkspaceId,
                        RepositoryId = target.RepositoryId,
                        RepositoryPath = target.RepositoryPath,
                        Snapshot = result.Snapshot,
                    };

                    // Every snapshot persist goes through the single-reader queue. Immediate and
                    // line-stats scans wait so the caller still sees the write when this method returns.
                    if (includeLineStats || persistImmediately)
                        await writeQueue.EnqueueAndWaitAsync(notification, cancellationToken);
                    else
                        writeQueue.Enqueue(notification);
                }
                else if (!result.Success)
                {
                    logger.Log(
                        result.ErrorCode is "PathUnderRemoval" or "RepositoryNotFound" ? LogLevel.Information : LogLevel.Warning,
                        "[GCDiag] Git Changes scan failed for {WorkspaceName}/{RepositoryName}: {ErrorCode} {ErrorMessage}",
                        target.WorkspaceName, target.RepositoryName, result.ErrorCode, result.ErrorMessage);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "Git Changes scan threw for {WorkspaceName}/{RepositoryName}",
                    target.WorkspaceName, target.RepositoryName);
            }
            finally
            {
                semaphore.Release();
            }

            if (!success)
            {
                Interlocked.Increment(ref failed);
            }

            var completedCount = Interlocked.Increment(ref completed);
            onProgress?.Invoke(new GitChangesWorkspaceScanProgress(target.RepositoryName, success, completedCount, total));
        });

        await Task.WhenAll(tasks);
        logger.LogInformation(
            "[GCDiag] scan done: workspace={WorkspaceId} context={ContextId} repos={Total} failedOrEmpty={Failed} elapsedMs={ElapsedMs} lineStats={LineStats} immediate={Immediate}",
            workspaceId, contextId.Value, total, failed, (long)System.Diagnostics.Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds, includeLineStats, persistImmediately);
    }
}
