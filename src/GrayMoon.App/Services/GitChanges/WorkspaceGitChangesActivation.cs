using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Ui;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Leases Git Changes activity for a workspace while any GrayMoon page under that workspace is open,
/// so background monitoring keeps its Worker-side watchers renewed and audited. A cold start (workspace
/// not already active) kicks off one circuit-scoped warm-up scan under
/// <see cref="WorkspaceJobKeys.GitChangesScanKey"/> instead of waiting for the next periodic sweep.
/// Called from the layout route binder, not from individual workspace pages.
/// </summary>
public interface IWorkspaceGitChangesActivation
{
    IDisposable Activate(int workspaceId);
}

public sealed class WorkspaceGitChangesActivation(
    IWorkspaceGitChangesActivityTracker activityTracker,
    IGitChangesWorkspaceScanner scanner,
    IWorkerBridge workerBridge,
    IBackgroundJobService jobService,
    IGitChangesAuditScheduler auditScheduler,
    IToastService toastService,
    IServiceScopeFactory scopeFactory,
    ILogger<WorkspaceGitChangesActivation> logger) : IWorkspaceGitChangesActivation
{
    public IDisposable Activate(int workspaceId)
    {
        var wasActive = activityTracker.IsActive(workspaceId);
        var lease = activityTracker.Subscribe(workspaceId);
        logger.LogInformation(
            "[GCDiag] workspace activation: workspace={WorkspaceId} wasActive={WasActive} workerConnected={WorkerConnected} (warm-up scan runs only when not previously active; special Workspace context here, Feature contexts are audited by the scheduler)",
            workspaceId, wasActive, workerBridge.IsWorkerConnected);

        if (wasActive || !workerBridge.IsWorkerConnected)
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
                await using var scope = scopeFactory.CreateAsyncScope();
                var contextResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();
                // Warm-up from workspace entry targets the special Workspace checkout.
                var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, ct);
                // Every Feature context is audited once; the special Workspace context is scanned by the warm-up below.
                foreach (var context in await contextResolver.ListForWorkspaceAsync(workspaceId, ct))
                {
                    if (!context.IsSpecialWorkspace)
                    {
                        auditScheduler.Request(workspaceId, context.ContextId, GitChangesAuditReason.Activation);
                    }
                }

                await scanner.ScanWorkspaceAsync(workspaceId, contextId, ct, progress =>
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
