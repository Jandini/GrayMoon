using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Background cleanup of removed Features' leftover folders (marked <c>GRAYMOON-PENDING-DELETE.md</c> by Remove Feature
/// when files were still in use). Silent: never notifies, toasts or asks; results only go to the log.
/// </summary>
public interface IFeatureFolderCleanupService
{
    /// <summary>Starts a sweep of one Workspace in the background, at most once per <c>MinInterval</c>. Never throws.</summary>
    void RequestSweep(int workspaceId);

    /// <summary>Sweeps every Workspace that has a managed Feature storage root (each one throttled like <see cref="RequestSweep"/>).</summary>
    Task SweepAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks the Worker (which owns the filesystem) to delete the marked folders under each Workspace's Feature storage root,
/// excluding every Feature that still exists. Runs when the Worker connects (so on every GrayMoon start) and when a
/// Workspace is opened. Skips a Workspace while a structural operation (Create / Remove Feature) holds it.
/// </summary>
public sealed class FeatureFolderCleanupService(
    IServiceScopeFactory scopeFactory,
    WorkerConnectionTracker connectionTracker,
    IWorkspaceOperationLock operationLock,
    ILogger<FeatureFolderCleanupService> logger) : IFeatureFolderCleanupService, IHostedService
{
    private readonly object _throttleLock = new();
    private readonly Dictionary<int, DateTime> _lastSweepUtc = [];

    /// <summary>Minimum gap between two sweeps of the same Workspace. Overridable in tests.</summary>
    internal TimeSpan MinInterval { get; set; } = TimeSpan.FromMinutes(15);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        connectionTracker.OnStateChanged(OnWorkerStateChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        connectionTracker.RemoveStateChanged(OnWorkerStateChanged);
        return Task.CompletedTask;
    }

    private void OnWorkerStateChanged(WorkerConnectionState state)
    {
        if (state != WorkerConnectionState.Online)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await SweepAllAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Feature folder cleanup failed after Worker connect");
            }
        });
    }

    public void RequestSweep(int workspaceId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SweepWorkspaceAsync(workspaceId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Feature folder cleanup failed. WorkspaceId={WorkspaceId}", workspaceId);
            }
        });
    }

    public async Task SweepAllAsync(CancellationToken cancellationToken = default)
    {
        List<int> workspaceIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            workspaceIds = await db.Workspaces.AsNoTracking()
                .Where(w => w.ManagedFeatureStorageRoot != null && w.ManagedFeatureStorageRoot != "")
                .Select(w => w.WorkspaceId)
                .ToListAsync(cancellationToken);
        }

        foreach (var workspaceId in workspaceIds)
            await SweepWorkspaceAsync(workspaceId, cancellationToken);
    }

    /// <summary>One sweep of one Workspace; returns false when it was skipped (throttled, busy, no Worker or no storage root).</summary>
    internal async Task<bool> SweepWorkspaceAsync(int workspaceId, CancellationToken cancellationToken)
    {
        if (operationLock.IsWorkspaceStructurallyBusy(workspaceId) || !TryClaimSweep(workspaceId))
            return false;

        await using var scope = scopeFactory.CreateAsyncScope();
        var workerBridge = scope.ServiceProvider.GetRequiredService<IWorkerBridge>();
        if (workerBridge.GetUnavailableReason() != null)
        {
            ReleaseClaim(workspaceId);
            return false;
        }

        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        string? storageRoot;
        string workspaceName;
        List<string> featureNames;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var workspace = await db.Workspaces.AsNoTracking()
                .Where(w => w.WorkspaceId == workspaceId)
                .Select(w => new { w.Name, w.ManagedFeatureStorageRoot })
                .FirstOrDefaultAsync(cancellationToken);
            storageRoot = workspace?.ManagedFeatureStorageRoot;
            workspaceName = workspace?.Name ?? "";
            // Every Feature that still exists, whatever its state, keeps its folder: a sweep only ever finishes a removal.
            featureNames = await db.WorkspaceFeatures.AsNoTracking()
                .Where(f => f.WorkspaceId == workspaceId)
                .Select(f => f.Name)
                .ToListAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(storageRoot) || WorkerPath.IsLegacyWindowsDriveRootGraymoonPath(storageRoot))
            return false;

        var response = await workerBridge.SendCommandAsync(
            WorkerHubMethods.SweepPendingFeatureFolders,
            new
            {
                featureStorageRoot = storageRoot,
                workspaceName,
                excludeFeatureNames = featureNames
            },
            cancellationToken);
        if (!response.Success)
        {
            logger.LogWarning(
                "Feature folder cleanup failed. WorkspaceId={WorkspaceId} Error={Error}",
                workspaceId, FeatureOperationLog.Redact(response.Error));
            return true;
        }

        var result = WorkerResponseJson.DeserializeWorkerResponse<SweepResult>(response.Data);
        if (result is { } counts && counts.Removed + counts.StillPending + counts.Refused > 0)
        {
            logger.LogInformation(
                "Feature folder cleanup. WorkspaceId={WorkspaceId} Removed={Removed} StillPending={StillPending} Refused={Refused}",
                workspaceId, counts.Removed, counts.StillPending, counts.Refused);
        }

        return true;
    }

    private bool TryClaimSweep(int workspaceId)
    {
        lock (_throttleLock)
        {
            var now = DateTime.UtcNow;
            if (_lastSweepUtc.TryGetValue(workspaceId, out var last) && now - last < MinInterval)
                return false;
            _lastSweepUtc[workspaceId] = now;
            return true;
        }
    }

    /// <summary>Lets the next request try again when this one could not even reach the Worker.</summary>
    private void ReleaseClaim(int workspaceId)
    {
        lock (_throttleLock)
            _lastSweepUtc.Remove(workspaceId);
    }

    /// <summary>App-side shape of the Worker's SweepPendingFeatureFolders response.</summary>
    private sealed class SweepResult
    {
        public int Removed { get; set; }
        public int StillPending { get; set; }
        public int Refused { get; set; }
    }
}
