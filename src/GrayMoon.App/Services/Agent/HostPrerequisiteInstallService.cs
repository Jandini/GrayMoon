using System.Text.Json;
using GrayMoon.App.Services.Ui;
using Microsoft.JSInterop;

namespace GrayMoon.App.Services.Agent;

/// <summary>
/// Circuit-scoped coordinator for Desktop "Install All" Host prerequisites.
/// Owns the WebView2 correlation callback so install continues and completes after the Agent page is navigated away.
/// </summary>
public sealed class HostPrerequisiteInstallService(
    IJSRuntime jsRuntime,
    IAgentBridge agentBridge,
    AgentConnectionTracker agentConnectionTracker,
    IToastService toastService,
    ILogger<HostPrerequisiteInstallService> logger) : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(90);

    private readonly object _gate = new();
    private DotNetObjectReference<HostPrerequisiteInstallService>? _desktopBridgeRef;
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

    public async Task StartAsync(IReadOnlyList<string> missingPrerequisiteIds)
    {
        ArgumentNullException.ThrowIfNull(missingPrerequisiteIds);
        if (missingPrerequisiteIds.Count == 0)
            return;

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
                "InstallHostPrerequisites",
                new { prerequisites = missingPrerequisiteIds.ToArray() },
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
            logger.LogWarning(ex, "Failed to start Host prerequisite installation");
            ClearPendingAndInstalling();
            toastService.ShowError("Could not start Host prerequisite installation.");
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
            logger.LogDebug(ex, "Best-effort unregister of Host prerequisite install request {RequestId}", id);
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
            await WaitForAgentReadyAsync(ReconnectTimeout);
            var versions = await TryLoadHostVersionsAsync();

            if (versions is not null && !HostPrerequisiteState.AnyMissing(versions))
            {
                toastService.Show("Host prerequisites installed.");
            }
            else if (success)
            {
                toastService.Show(string.IsNullOrWhiteSpace(message) ? "Host prerequisites installed." : message);
            }
            else
            {
                toastService.ShowError(BuildFailureDetail(versions, failedPrerequisiteIds, message));
            }
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
            logger.LogDebug(ex, "Best-effort dispose of Host prerequisite desktop bridge");
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

    private async Task WaitForAgentReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (agentConnectionTracker.State == AgentConnectionState.Online && agentBridge.IsAgentConnected)
                return;

            await Task.Delay(500);
        }
    }

    private async Task<HostPrerequisiteVersions?> TryLoadHostVersionsAsync()
    {
        if (!agentBridge.IsAgentConnected)
            return null;

        var response = await agentBridge.SendCommandAsync("GetHostInfo", new { }, CancellationToken.None);
        if (!response.Success || response.Data is null)
            return null;

        try
        {
            var json = response.Data is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(response.Data);
            var dto = JsonSerializer.Deserialize<HostInfoDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return dto is null
                ? null
                : new HostPrerequisiteVersions(dto.DotnetVersion, dto.GitVersion, dto.GitVersionToolVersion);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse GetHostInfo after Host prerequisite install");
            return null;
        }
    }

    private static string BuildFailureDetail(
        HostPrerequisiteVersions? versions,
        string[]? failedPrerequisiteIds,
        string? message)
    {
        var namedFailures = (failedPrerequisiteIds ?? [])
            .Select(HostPrerequisiteState.DisplayName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var remaining = versions is null
            ? namedFailures
            : HostPrerequisiteState.GetMissingIds(versions).Select(HostPrerequisiteState.DisplayName).ToArray();

        if (remaining.Length > 0)
            return $"Still missing: {string.Join(", ", remaining)}.";
        if (namedFailures.Length > 0)
            return $"Failed to install: {string.Join(", ", namedFailures)}.";
        return message ?? "Host prerequisite installation did not complete successfully.";
    }

    private sealed record HostInfoDto(string? DotnetVersion, string? GitVersion, string? GitVersionToolVersion);
}
