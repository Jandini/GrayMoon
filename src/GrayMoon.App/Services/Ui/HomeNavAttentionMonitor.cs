using GrayMoon.App.Services.Worker;

namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Circuit-scoped source for <c>WorkerUpgradeNotificationDot</c>. Follows the Worker connection only:
/// the circle appears when the Worker goes down and disappears when it comes back, with no host check.
/// </summary>
public sealed class HomeNavAttentionMonitor : IDisposable
{
    private readonly WorkerConnectionTracker _workerConnectionTracker;
    private bool _started;
    private bool _disposed;
    private bool _ignoreInitialState;

    public HomeNavAttentionMonitor(WorkerConnectionTracker workerConnectionTracker)
    {
        _workerConnectionTracker = workerConnectionTracker;
    }

    public event Action? Changed;

    public bool ShowDot => HomeNavNotification.ShouldShow(
        _workerConnectionTracker.State,
        _workerConnectionTracker.IsSelfUpdateInProgress);

    public string Title => HomeNavNotification.Title(
        _workerConnectionTracker.State,
        _workerConnectionTracker.IsSelfUpdateInProgress);

    public void EnsureStarted()
    {
        if (_started || _disposed)
            return;

        _started = true;
        _ignoreInitialState = true;
        _workerConnectionTracker.OnStateChanged(OnWorkerStateChanged);
        _ignoreInitialState = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _workerConnectionTracker.RemoveStateChanged(OnWorkerStateChanged);
    }

    private void OnWorkerStateChanged(WorkerConnectionState _)
    {
        if (_ignoreInitialState || _disposed)
            return;

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_disposed)
            return;

        Changed?.Invoke();
    }
}
