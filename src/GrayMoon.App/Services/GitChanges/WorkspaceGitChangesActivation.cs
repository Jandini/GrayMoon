using GrayMoon.App.Services.Agent;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Ui;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Leases Git Changes activity for a workspace while Repositories or Changes is open, so
/// background monitoring sweeps it and Agent-side watchers stay warm. A cold start (workspace
/// not already active) kicks off one circuit-scoped warm-up scan under
/// <see cref="WorkspaceJobKeys.GitChangesScanKey"/> instead of waiting for the next periodic sweep.
/// </summary>
public interface IWorkspaceGitChangesActivation
{
    IDisposable Activate(int workspaceId);
}

public sealed class WorkspaceGitChangesActivation(
    IWorkspaceGitChangesActivityTracker activityTracker,
    IGitChangesWorkspaceScanner scanner,
    IAgentBridge agentBridge,
    IBackgroundJobService jobService,
    IToastService toastService,
    ILogger<WorkspaceGitChangesActivation> logger) : IWorkspaceGitChangesActivation
{
    public IDisposable Activate(int workspaceId)
    {
        var wasActive = activityTracker.IsActive(workspaceId);
        var lease = activityTracker.Subscribe(workspaceId);

        if (wasActive || !agentBridge.IsAgentConnected)
        {
            return lease;
        }

        var scanKey = WorkspaceJobKeys.GitChangesScanKey(workspaceId);
        if (jobService.IsRunning(scanKey) || jobService.IsRunning(WorkspaceJobKeys.GitChangesPageKey(workspaceId)))
        {
            return lease;
        }

        jobService.StartJob(scanKey, "Refreshing repositories...", async (job, ct) =>
        {
            try
            {
                await scanner.ScanWorkspaceAsync(workspaceId, ct, progress =>
                    job.ReportProgress($"Refreshing {progress.Completed} of {progress.Total} repositories..."), includeLineStats: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Git Changes refresh failed for workspace {WorkspaceId}", workspaceId);
                toastService.ShowError("Refresh failed. See logs for details.");
                throw;
            }
        });

        return lease;
    }
}
