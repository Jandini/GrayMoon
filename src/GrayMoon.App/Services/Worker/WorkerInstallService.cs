using GrayMoon.App.Services.Ui;
using Microsoft.JSInterop;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Circuit-scoped coordinator for the Desktop "Install" worker button.
/// Uses the same WebView2 correlation path as <see cref="HostPrerequisiteInstallService"/>
/// so install continues and completes after the Worker page is navigated away.
/// Desktop owns the install script and opens it in a visible PowerShell window.
/// </summary>
public sealed class WorkerInstallService(
    IJSRuntime jsRuntime,
    IWorkerBridge workerBridge,
    WorkerConnectionTracker workerConnectionTracker,
    IToastService toastService,
    ILogger<WorkerInstallService> logger) : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(90);

    private readonly object _gate = new();
    private DotNetObjectReference<WorkerInstallService>? _desktopBridgeRef;
    private string? _pendingRequestId;
    private bool _isInstalling;
    private bool _disposed;

    public bool IsInstalling
    {
        get
        {
            lock (_gate)
                return _isInstalling;
        }
    }

    /// <summary>Raised when install starts or finishes (may run off the renderer sync context).</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_disposed || _isInstalling)
                return;
            _isInstalling = true;
        }

        RaiseChanged();

        try
        {
            _desktopBridgeRef ??= DotNetObjectReference.Create(this);
            var requestId = Guid.NewGuid().ToString("N");

            lock (_gate)
                _pendingRequestId = requestId;

            await jsRuntime.InvokeVoidAsync("graymoonDesktopBridge.registerPending", requestId, _desktopBridgeRef);
            var posted = await jsRuntime.InvokeAsync<bool>(
                "graymoonDesktopBridge.postCommand",
                "InstallWorker",
                new { },
                requestId);

            if (!posted)
            {
                ClearPendingAndInstalling();
                toastService.ShowError("GrayMoon Desktop bridge is not available.");
                RaiseChanged();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to start worker installation");
            ClearPendingAndInstalling();
            toastService.ShowError("Could not start worker installation.");
            RaiseChanged();
        }
    }

    [JSInvokable]
    public async Task OnNativeCommandResult(
        string id,
        bool success,
        bool cancelled,
        string? message,
        string[]? failedPrerequisiteIds)
    {
        _ = failedPrerequisiteIds;

        lock (_gate)
        {
            if (!string.Equals(id, _pendingRequestId, StringComparison.Ordinal))
                return;
            _pendingRequestId = null;
        }

        try
        {
            await jsRuntime.InvokeVoidAsync("graymoonDesktopBridge.unregisterPending", id);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Best-effort unregister of worker install request {RequestId}", id);
        }

        if (cancelled)
        {
            SetInstalling(false);
            toastService.Show(string.IsNullOrWhiteSpace(message) ? "Installation cancelled." : message);
            RaiseChanged();
            return;
        }

        try
        {
            if (!success)
            {
                toastService.ShowError(string.IsNullOrWhiteSpace(message) ? "Worker installation failed." : message);
                return;
            }

            await WaitForWorkerReadyAsync(ReconnectTimeout);
            if (workerConnectionTracker.State == WorkerConnectionState.Online && workerBridge.IsWorkerConnected)
                toastService.Show("Worker installed.");
            else
                toastService.Show("Worker install finished, but the worker has not connected yet.");
        }
        finally
        {
            SetInstalling(false);
            RaiseChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pendingRequestId = null;
            _isInstalling = false;
        }

        if (_desktopBridgeRef is null)
            return;

        try
        {
            await jsRuntime.InvokeVoidAsync("graymoonDesktopBridge.disposeAll", _desktopBridgeRef);
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Best-effort dispose of worker install desktop bridge");
        }

        _desktopBridgeRef.Dispose();
        _desktopBridgeRef = null;
    }

    private void ClearPendingAndInstalling()
    {
        lock (_gate)
        {
            _pendingRequestId = null;
            _isInstalling = false;
        }
    }

    private void SetInstalling(bool value)
    {
        lock (_gate)
            _isInstalling = value;
    }

    private void RaiseChanged() => Changed?.Invoke();

    private async Task WaitForWorkerReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (workerConnectionTracker.State == WorkerConnectionState.Online && workerBridge.IsWorkerConnected)
                return;

            await Task.Delay(500);
        }
    }
}
