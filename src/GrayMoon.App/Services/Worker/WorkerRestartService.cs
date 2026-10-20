using GrayMoon.App.Services.Ui;
using Microsoft.JSInterop;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Circuit-scoped coordinator for the Desktop Host tab "Restart" button.
/// Restarts the Worker Windows service so a CLI installed afterwards, such as an AI agent, is on its PATH.
/// </summary>
public sealed class WorkerRestartService(
    IJSRuntime jsRuntime,
    IWorkerBridge workerBridge,
    WorkerConnectionTracker workerConnectionTracker,
    IToastService toastService,
    ILogger<WorkerRestartService> logger) : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(90);

    private readonly object _gate = new();
    private DotNetObjectReference<WorkerRestartService>? _desktopBridgeRef;
    private string? _pendingRequestId;
    private bool _isRestarting;
    private bool _disposed;

    public bool IsRestarting
    {
        get
        {
            lock (_gate)
                return _isRestarting;
        }
    }

    /// <summary>Raised when a restart starts or finishes (may run off the renderer sync context).</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_disposed || _isRestarting)
                return;
            _isRestarting = true;
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
                "RestartWorker",
                new { },
                requestId);

            if (!posted)
            {
                ClearPending();
                toastService.ShowError("GrayMoon Desktop bridge is not available.");
                RaiseChanged();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to start worker restart");
            ClearPending();
            toastService.ShowError("Could not restart the Worker.");
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
            logger.LogDebug(ex, "Best-effort unregister of worker restart request {RequestId}", id);
        }

        if (cancelled)
        {
            SetRestarting(false);
            toastService.Show(string.IsNullOrWhiteSpace(message) ? "Restart cancelled." : message);
            RaiseChanged();
            return;
        }

        try
        {
            if (!success)
            {
                toastService.ShowError(string.IsNullOrWhiteSpace(message) ? "The Worker could not be restarted." : message);
                return;
            }

            await WaitForWorkerReadyAsync(ReconnectTimeout);
            if (workerConnectionTracker.State == WorkerConnectionState.Online && workerBridge.IsWorkerConnected)
                toastService.Show("Worker restarted.");
            else
                toastService.Show("Worker restart finished, but the worker has not connected yet.");
        }
        finally
        {
            SetRestarting(false);
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
            _isRestarting = false;
        }

        if (_desktopBridgeRef is null)
            return;

        try
        {
            await jsRuntime.InvokeVoidAsync("graymoonDesktopBridge.disposeAll", _desktopBridgeRef);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Best-effort dispose of worker restart desktop bridge");
        }

        _desktopBridgeRef.Dispose();
        _desktopBridgeRef = null;
    }

    private void ClearPending()
    {
        lock (_gate)
        {
            _pendingRequestId = null;
            _isRestarting = false;
        }
    }

    private void SetRestarting(bool value)
    {
        lock (_gate)
            _isRestarting = value;
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
