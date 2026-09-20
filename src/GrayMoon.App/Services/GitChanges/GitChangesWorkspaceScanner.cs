using GrayMoon.App.Data;
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
/// parallelism, and pushes each successful result through the write queue or immediately when
/// line stats were requested.
/// </summary>
public interface IGitChangesWorkspaceScanner
{
    Task ScanWorkspaceAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken,
        Action<GitChangesWorkspaceScanProgress>? onProgress = null,
        bool includeLineStats = false,
        int? repositoryId = null);
}

public sealed class GitChangesWorkspaceScanner(
    IServiceScopeFactory scopeFactory,
    IOptions<GitChangesOptions> gitChangesOptions,
    ILogger<GitChangesWorkspaceScanner> logger) : IGitChangesWorkspaceScanner
{
    public async Task ScanWorkspaceAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken,
        Action<GitChangesWorkspaceScanProgress>? onProgress = null,
        bool includeLineStats = false,
        int? repositoryId = null)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var agentBridge = scope.ServiceProvider.GetRequiredService<IAgentBridge>();
        if (!agentBridge.IsAgentConnected)
        {
            return;
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var agentClient = scope.ServiceProvider.GetRequiredService<IGitChangesAgentClient>();
        var writeQueue = scope.ServiceProvider.GetRequiredService<WorkspaceGitChangesWriteQueue>();
        var pushHandler = scope.ServiceProvider.GetRequiredService<GitChangesSnapshotPushHandler>();

        var linksQuery = dbContext.WorkspaceRepositories
            .Where(l => l.WorkspaceId == workspaceId);
        if (repositoryId.HasValue)
        {
            linksQuery = linksQuery.Where(l => l.RepositoryId == repositoryId.Value);
        }

        var links = await linksQuery
            .Include(l => l.Workspace)
            .Include(l => l.Repository)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var (root, workspaceFolderName) = await pathResolver.GetAgentWorkspaceArgsAsync(contextId, cancellationToken);

        var targets = new List<MonitorTarget>();
        foreach (var link in links)
        {
            if (link.Workspace == null || link.Repository == null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            targets.Add(new MonitorTarget(root, workspaceFolderName, link.Repository.RepositoryName, link.WorkspaceId, link.RepositoryId));
        }

        if (targets.Count == 0)
        {
            return;
        }

        var completed = 0;
        var total = targets.Count;
        using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(gitChangesOptions.Value.MaxParallelRepositoryOperations, total)));

        var tasks = targets.Select(async target =>
        {
            await semaphore.WaitAsync(cancellationToken);
            var success = false;
            try
            {
                var result = await agentClient.GetStatusAsync(
                    target.Root, target.WorkspaceName, target.RepositoryName,
                    target.WorkspaceId, target.RepositoryId, cancellationToken,
                    includeLineStats);

                if (result.Success && result.Snapshot != null)
                {
                    success = true;
                    var notification = new GitChangesSnapshotNotification
                    {
                        WorkspaceId = target.WorkspaceId,
                        RepositoryId = target.RepositoryId,
                        RepositoryPath = target.Root,
                        Snapshot = result.Snapshot,
                    };

                    // Refresh / warm-up persist immediately so LoadAsync after the scan sees +/-.
                    // Watcher and background sweeps stay on the write queue.
                    if (includeLineStats)
                    {
                        await pushHandler.HandleAsync(notification, cancellationToken);
                    }
                    else
                    {
                        writeQueue.Enqueue(notification);
                    }
                }
                else if (!result.Success)
                {
                    logger.LogDebug(
                        "Git Changes scan failed for {WorkspaceName}/{RepositoryName}: {ErrorCode} {ErrorMessage}",
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

            var completedCount = Interlocked.Increment(ref completed);
            onProgress?.Invoke(new GitChangesWorkspaceScanProgress(target.RepositoryName, success, completedCount, total));
        });

        await Task.WhenAll(tasks);
    }

    private sealed record MonitorTarget(string Root, string WorkspaceName, string RepositoryName, int WorkspaceId, int RepositoryId);
}
