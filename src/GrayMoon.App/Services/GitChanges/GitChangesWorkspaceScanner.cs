using GrayMoon.App.Data;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>Reported once per repository as a workspace scan progresses.</summary>
public sealed record GitChangesWorkspaceScanProgress(string RepositoryName, bool Success, int Completed, int Total);

/// <summary>
/// The single Git Changes status-scan routine for one workspace, shared by the periodic background
/// sweep, the on-open warm-up scan, and the manual Refresh button. Calls <c>GetGitChangeStatus</c> for
/// every repository in the workspace with bounded parallelism, and pushes each successful result
/// through the same <see cref="WorkspaceGitChangesWriteQueue"/> used by watcher-driven pushes.
/// </summary>
public interface IGitChangesWorkspaceScanner
{
    Task ScanWorkspaceAsync(int workspaceId, CancellationToken cancellationToken, Action<GitChangesWorkspaceScanProgress>? onProgress = null);
}

public sealed class GitChangesWorkspaceScanner(
    IServiceScopeFactory scopeFactory,
    IOptions<GitChangesOptions> gitChangesOptions,
    ILogger<GitChangesWorkspaceScanner> logger) : IGitChangesWorkspaceScanner
{
    public async Task ScanWorkspaceAsync(int workspaceId, CancellationToken cancellationToken, Action<GitChangesWorkspaceScanProgress>? onProgress = null)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var agentBridge = scope.ServiceProvider.GetRequiredService<IAgentBridge>();
        if (!agentBridge.IsAgentConnected)
        {
            return;
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workspaceService = scope.ServiceProvider.GetRequiredService<WorkspaceService>();
        var agentClient = scope.ServiceProvider.GetRequiredService<IGitChangesAgentClient>();
        var writeQueue = scope.ServiceProvider.GetRequiredService<WorkspaceGitChangesWriteQueue>();

        var links = await dbContext.WorkspaceRepositories
            .Where(l => l.WorkspaceId == workspaceId)
            .Include(l => l.Workspace)
            .Include(l => l.Repository)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var targets = new List<MonitorTarget>();
        foreach (var link in links)
        {
            if (link.Workspace == null || link.Repository == null)
            {
                continue;
            }

            var root = await workspaceService.GetRootPathForWorkspaceAsync(link.Workspace, cancellationToken);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            targets.Add(new MonitorTarget(root, link.Workspace.Name, link.Repository.RepositoryName, link.WorkspaceId, link.RepositoryId));
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
                    target.WorkspaceId, target.RepositoryId, cancellationToken);

                if (result.Success && result.Snapshot != null)
                {
                    success = true;
                    writeQueue.Enqueue(new GitChangesSnapshotNotification
                    {
                        WorkspaceId = target.WorkspaceId,
                        RepositoryId = target.RepositoryId,
                        Snapshot = result.Snapshot,
                    });
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
