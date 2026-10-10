using GrayMoon.App.Hubs;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Offers the Worker upgrade dialog once per GrayMoon.App process, and only for a version
/// mismatch seen before any matching Worker has connected. Later mismatches stay on the
/// notification path.
/// </summary>
public sealed class WorkerUpgradeStartupPrompt
{
    private readonly object _gate = new();
    private int _desktopClients;
    private bool _observedMismatch;
    private bool _seenMatchingWorker;
    private bool _emitted;
    private WorkerUpgradePrompt? _prompt;

    /// <summary>
    /// Records a worker connection change. <see cref="WorkerUpgradeStartupObservation.SuppressUpdateNotification"/>
    /// is set for the one startup mismatch so the tray balloon is not also shown.
    /// <see cref="WorkerUpgradeStartupObservation.Prompt"/> is set only when a Desktop client is
    /// already connected and can be told immediately.
    /// </summary>
    public WorkerUpgradeStartupObservation Observe(
        WorkerConnectionState state,
        bool selfUpdateInProgress,
        string? workerSemVer,
        string? appSemVer)
    {
        if (state == WorkerConnectionState.Online)
        {
            lock (_gate)
            {
                _seenMatchingWorker = true;
                if (!_emitted)
                    _prompt = null;
            }

            return default;
        }

        if (selfUpdateInProgress || state != WorkerConnectionState.VersionMismatch)
            return default;

        lock (_gate)
        {
            if (_seenMatchingWorker || _observedMismatch)
                return default;

            _observedMismatch = true;
            _prompt = new WorkerUpgradePrompt(workerSemVer, appSemVer);
            return new WorkerUpgradeStartupObservation(SuppressUpdateNotification: true, TryEmitLocked());
        }
    }

    /// <summary>Catch-up for a Desktop client that connects after the startup mismatch was seen.</summary>
    public WorkerUpgradePrompt? OnDesktopConnected()
    {
        lock (_gate)
        {
            _desktopClients++;
            return TryEmitLocked();
        }
    }

    public void OnDesktopDisconnected()
    {
        lock (_gate)
        {
            if (_desktopClients > 0)
                _desktopClients--;
        }
    }

    private WorkerUpgradePrompt? TryEmitLocked()
    {
        if (_emitted || _prompt is null || _desktopClients <= 0)
            return null;

        _emitted = true;
        return _prompt;
    }
}

public readonly record struct WorkerUpgradeStartupObservation(
    bool SuppressUpdateNotification,
    WorkerUpgradePrompt? Prompt);
