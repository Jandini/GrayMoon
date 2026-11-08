using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// Starts the retry timer for a Workspace that has a folder left pending deletion (no-op when it already runs). Every
    /// <c>RetryInterval</c> it sweeps that Workspace, and it stops by itself once a sweep leaves nothing pending. Never throws.
    /// </summary>
    void ArmRetry(int workspaceId);
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
    IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<FeatureFolderCleanupService> logger) : IFeatureFolderCleanupService, IHostedService
{
    private readonly object _throttleLock = new();
    private readonly Dictionary<int, DateTime> _lastSweepUtc = [];

    /// <summary>Minimum gap between two sweeps of the same Workspace. Overridable in tests.</summary>
    internal TimeSpan MinInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Gap between retry sweeps of a Workspace that still has a folder pending deletion. Overridable in tests.</summary>
    internal TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>One retry loop per Workspace that has something pending; guarded by <see cref="_throttleLock"/>.</summary>
    private readonly Dictionary<int, CancellationTokenSource> _retryLoops = [];

    /// <summary>What one sweep of one Workspace found.</summary>
    internal enum SweepState
    {
        /// <summary>Not run (throttled, busy or Worker unavailable); nothing learned, try again later.</summary>
        Skipped,
        /// <summary>The Workspace has nothing to sweep (no managed storage root, or it no longer exists).</summary>
        NotApplicable,
        /// <summary>The Worker could not be asked or its answer was unusable; the state is unknown.</summary>
        Failed,
        /// <summary>The sweep ran and no folder is left pending deletion.</summary>
        Clean,
        /// <summary>The sweep ran and at least one folder is still in use.</summary>
        StillPending,
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        connectionTracker.OnStateChanged(OnWorkerStateChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        connectionTracker.RemoveStateChanged(OnWorkerStateChanged);
        List<CancellationTokenSource> loops;
        lock (_throttleLock)
        {
            loops = [.. _retryLoops.Values];
            _retryLoops.Clear();
        }

        foreach (var loop in loops)
            loop.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>True while the Workspace's retry loop is running.</summary>
    internal bool IsRetrying(int workspaceId)
    {
        lock (_throttleLock)
            return _retryLoops.ContainsKey(workspaceId);
    }

    public void ArmRetry(int workspaceId)
    {
        CancellationTokenSource loop;
        lock (_throttleLock)
        {
            if (_retryLoops.ContainsKey(workspaceId))
                return;
            loop = new CancellationTokenSource();
            _retryLoops[workspaceId] = loop;
        }

        logger.LogInformation(
            "Feature folder cleanup retry started. WorkspaceId={WorkspaceId} Every={Minutes} min", workspaceId, RetryInterval.TotalMinutes);
        _ = Task.Run(() => RunRetryLoopAsync(workspaceId, loop));
    }

    /// <summary>Sweeps one Workspace every <see cref="RetryInterval"/> until a sweep finds nothing pending (or the service stops).</summary>
    private async Task RunRetryLoopAsync(int workspaceId, CancellationTokenSource loop)
    {
        try
        {
            while (!loop.IsCancellationRequested)
            {
                await Task.Delay(RetryInterval, loop.Token);

                SweepState state;
                try
                {
                    state = await SweepWorkspaceCoreAsync(workspaceId, ignoreThrottle: true, loop.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Feature folder cleanup retry failed. WorkspaceId={WorkspaceId}", workspaceId);
                    continue;
                }

                // Clean and NotApplicable already stopped this loop (see ObserveSweep); anything else keeps it running.
                if (state is SweepState.Clean or SweepState.NotApplicable)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped: service shutting down, or a sweep elsewhere found nothing left.
        }
        finally
        {
            StopRetry(workspaceId, loop);
        }
    }

    /// <summary>Stops and forgets the Workspace's retry loop. With <paramref name="only"/>, only if that exact loop is still the registered one.</summary>
    private void StopRetry(int workspaceId, CancellationTokenSource? only = null)
    {
        CancellationTokenSource? loop;
        lock (_throttleLock)
        {
            if (!_retryLoops.TryGetValue(workspaceId, out loop) || (only != null && !ReferenceEquals(loop, only)))
                loop = null;
            else
                _retryLoops.Remove(workspaceId);
        }

        if (loop == null)
            return;

        logger.LogInformation("Feature folder cleanup retry stopped. WorkspaceId={WorkspaceId}", workspaceId);
        loop.Cancel();
    }

    /// <summary>Starts the retry loop when a sweep left something pending; stops it when nothing is left to retry.</summary>
    private void ObserveSweep(int workspaceId, SweepState state)
    {
        switch (state)
        {
            case SweepState.StillPending:
                ArmRetry(workspaceId);
                break;
            case SweepState.Clean:
            case SweepState.NotApplicable:
                StopRetry(workspaceId);
                break;
        }
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

        // Workspaces are independent: SweepWorkspaceAsync claims/throttles per Workspace and uses its own scope.
        using var gate = new SemaphoreSlim(Math.Max(1, workspaceOptions.Value.MaxParallelOperations));
        var tasks = workspaceIds.Select(async workspaceId =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await SweepWorkspaceAsync(workspaceId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Feature folder cleanup failed. WorkspaceId={WorkspaceId}", workspaceId);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>One sweep of one Workspace; returns false when it was skipped (throttled, busy, no Worker or no storage root).</summary>
    internal async Task<bool> SweepWorkspaceAsync(int workspaceId, CancellationToken cancellationToken) =>
        await SweepWorkspaceCoreAsync(workspaceId, ignoreThrottle: false, cancellationToken) is not (SweepState.Skipped or SweepState.NotApplicable);

    /// <summary>
    /// One sweep of one Workspace. <paramref name="ignoreThrottle"/> is for the retry timer (its interval is longer than the
    /// throttle anyway). Every result is fed to <see cref="ObserveSweep"/>, so a sweep from any trigger arms the retry timer
    /// when something is still pending and stops it when nothing is.
    /// </summary>
    internal async Task<SweepState> SweepWorkspaceCoreAsync(int workspaceId, bool ignoreThrottle, CancellationToken cancellationToken)
    {
        var state = await SweepWorkspaceOnceAsync(workspaceId, ignoreThrottle, cancellationToken);
        ObserveSweep(workspaceId, state);
        return state;
    }

    private async Task<SweepState> SweepWorkspaceOnceAsync(int workspaceId, bool ignoreThrottle, CancellationToken cancellationToken)
    {
        if (operationLock.IsWorkspaceStructurallyBusy(workspaceId) || !TryClaimSweep(workspaceId, ignoreThrottle))
            return SweepState.Skipped;

        await using var scope = scopeFactory.CreateAsyncScope();
        var workerBridge = scope.ServiceProvider.GetRequiredService<IWorkerBridge>();
        if (workerBridge.GetUnavailableReason() != null)
        {
            ReleaseClaim(workspaceId);
            return SweepState.Skipped;
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
            return SweepState.NotApplicable;

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
            return SweepState.Failed;
        }

        var result = WorkerResponseJson.DeserializeWorkerResponse<SweepResult>(response.Data);
        if (result is not { } counts)
        {
            logger.LogWarning("Feature folder cleanup got an unreadable answer from the Worker. WorkspaceId={WorkspaceId}", workspaceId);
            return SweepState.Failed;
        }

        if (counts.Removed + counts.StillPending + counts.Refused > 0)
        {
            logger.LogInformation(
                "Feature folder cleanup. WorkspaceId={WorkspaceId} Removed={Removed} StillPending={StillPending} Refused={Refused}",
                workspaceId, counts.Removed, counts.StillPending, counts.Refused);
        }

        return counts.StillPending > 0 ? SweepState.StillPending : SweepState.Clean;
    }

    private bool TryClaimSweep(int workspaceId, bool ignoreThrottle)
    {
        lock (_throttleLock)
        {
            var now = DateTime.UtcNow;
            if (!ignoreThrottle && _lastSweepUtc.TryGetValue(workspaceId, out var last) && now - last < MinInterval)
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
