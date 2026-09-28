using System.Text.Json;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Agent;
using GrayMoon.App.Services.Connectors;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Circuit-scoped source for <c>AgentUpgradeNotificationDot</c>. One instance owns the
/// worker, prerequisite, and connector checks so the static nav can host the same
/// interactive dot the worktree branch uses.
/// </summary>
public sealed class HomeNavAttentionMonitor : IDisposable
{
    private readonly AgentConnectionTracker _agentConnectionTracker;
    private readonly IAgentBridge _agentBridge;
    private readonly ConnectorRepository _connectorRepository;
    private readonly ConnectorHealthService _connectorHealthService;
    private readonly HostPrerequisiteInstallService _hostPrerequisiteInstall;
    private readonly NavigationManager _navigationManager;
    private readonly ILogger<HomeNavAttentionMonitor> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _hostInfoGeneration;
    private bool _started;
    private bool _disposed;
    private bool _ignoreInitialState;
    private bool _hasConnectors = true;
    private bool _anyUsedConnectorUnhealthy;
    private bool _hostPrerequisitesMissing;

    public HomeNavAttentionMonitor(
        AgentConnectionTracker agentConnectionTracker,
        IAgentBridge agentBridge,
        ConnectorRepository connectorRepository,
        ConnectorHealthService connectorHealthService,
        HostPrerequisiteInstallService hostPrerequisiteInstall,
        NavigationManager navigationManager,
        ILogger<HomeNavAttentionMonitor> logger)
    {
        _agentConnectionTracker = agentConnectionTracker;
        _agentBridge = agentBridge;
        _connectorRepository = connectorRepository;
        _connectorHealthService = connectorHealthService;
        _hostPrerequisiteInstall = hostPrerequisiteInstall;
        _navigationManager = navigationManager;
        _logger = logger;
    }

    public event Action? Changed;

    public bool ShowDot => HomeNavNotification.ShouldShow(
        _agentConnectionTracker.State,
        _agentConnectionTracker.IsSelfUpdateInProgress,
        _hostPrerequisitesMissing,
        HomeNavNotification.ConnectorsRequired(_hasConnectors, _anyUsedConnectorUnhealthy));

    public string Title => HomeNavNotification.Title(
        _agentConnectionTracker.State,
        _agentConnectionTracker.IsSelfUpdateInProgress,
        _hostPrerequisitesMissing,
        _hasConnectors,
        _anyUsedConnectorUnhealthy);

    public void EnsureStarted()
    {
        if (_started || _disposed)
            return;

        _started = true;
        _ignoreInitialState = true;
        _agentConnectionTracker.OnStateChanged(OnAgentStateChanged);
        _ignoreInitialState = false;
        _navigationManager.LocationChanged += OnLocationChanged;
        _hostPrerequisiteInstall.Changed += OnHostPrerequisitesChanged;
        _ = RefreshAllAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Interlocked.Increment(ref _hostInfoGeneration);
        _agentConnectionTracker.RemoveStateChanged(OnAgentStateChanged);
        _navigationManager.LocationChanged -= OnLocationChanged;
        _hostPrerequisiteInstall.Changed -= OnHostPrerequisitesChanged;
    }

    private void OnAgentStateChanged(AgentConnectionState state)
    {
        if (_ignoreInitialState || _disposed)
            return;

        _ = OnAgentStateChangedAsync(state);
    }

    private async Task OnAgentStateChangedAsync(AgentConnectionState state)
    {
        if (_disposed)
            return;

        if (state != AgentConnectionState.Online)
        {
            Interlocked.Increment(ref _hostInfoGeneration);
            _hostPrerequisitesMissing = false;
            RaiseChanged();
            return;
        }

        await RefreshHostPrerequisitesAsync();
        RaiseChanged();
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_disposed)
            return;

        _ = RefreshAfterNavigationAsync();
    }

    private void OnHostPrerequisitesChanged()
    {
        if (_disposed || _hostPrerequisiteInstall.IsInstalling)
            return;

        if (_agentConnectionTracker.State != AgentConnectionState.Online)
            return;

        _ = RefreshHostPrerequisitesAsync();
    }

    private async Task RefreshAllAsync()
    {
        await RefreshConnectorsAsync();
        if (_disposed)
            return;

        if (_agentConnectionTracker.State == AgentConnectionState.Online)
            await RefreshHostPrerequisitesAsync();
    }

    private async Task RefreshAfterNavigationAsync()
    {
        await RefreshConnectorsAsync();
        if (_disposed || !_hostPrerequisitesMissing)
            return;

        if (_agentConnectionTracker.State == AgentConnectionState.Online)
            await RefreshHostPrerequisitesAsync();
    }

    private async Task RefreshConnectorsAsync()
    {
        if (_disposed)
            return;

        var changed = false;
        try
        {
            await _refreshGate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed)
                return;

            var connectors = await _connectorRepository.GetAllAsync();
            var hasConnectors = connectors.Count > 0;
            var unhealthy = hasConnectors && await _connectorHealthService.AnyUsedConnectorUnhealthyAsync();
            if (_disposed)
                return;

            changed = _hasConnectors != hasConnectors || _anyUsedConnectorUnhealthy != unhealthy;
            _hasConnectors = hasConnectors;
            _anyUsedConnectorUnhealthy = unhealthy;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh connector attention for the Home nav dot");
            return;
        }
        finally
        {
            try
            {
                _refreshGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (changed)
            RaiseChanged();
    }

    private async Task RefreshHostPrerequisitesAsync()
    {
        var generation = Interlocked.Increment(ref _hostInfoGeneration);
        var missing = await QueryHostPrerequisitesMissingAsync();
        if (generation != Volatile.Read(ref _hostInfoGeneration) || _disposed)
            return;

        var nowMissing = _agentConnectionTracker.State == AgentConnectionState.Online && missing;
        if (_hostPrerequisitesMissing == nowMissing)
            return;

        _hostPrerequisitesMissing = nowMissing;
        RaiseChanged();
    }

    private async Task<bool> QueryHostPrerequisitesMissingAsync()
    {
        if (!_agentBridge.IsAgentConnected)
            return false;

        try
        {
            var response = await _agentBridge.SendCommandAsync("GetHostInfo", new { }, CancellationToken.None);
            if (!response.Success || response.Data is null)
                return false;

            var json = response.Data is JsonElement element
                ? element.GetRawText()
                : JsonSerializer.Serialize(response.Data);
            var dto = JsonSerializer.Deserialize<HostInfoDto>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto is null)
                return false;

            return HostPrerequisiteState.AnyMissing(new HostPrerequisiteVersions(
                dto.DotnetVersion,
                dto.GitVersion,
                dto.GitVersionToolVersion));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void RaiseChanged()
    {
        if (_disposed)
            return;

        Changed?.Invoke();
    }

    private sealed record HostInfoDto(string? DotnetVersion, string? GitVersion, string? GitVersionToolVersion);
}
