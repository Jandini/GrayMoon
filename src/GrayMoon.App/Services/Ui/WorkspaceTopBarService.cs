using System.Threading;
using GrayMoon.App.Hubs;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Keeps the main header strip in sync with the current workspace route so the layout can show the workspace name without each page pushing state.</summary>
public interface IWorkspaceTopBarService
{
    /// <summary>Display name for the current workspace route, or null when not under <c>/workspaces/{id}</c>.</summary>
    string? WorkspaceDisplayName { get; }

    event Action? Changed;

    Task EnsureSynchronizedAsync();
}

public sealed class WorkspaceTopBarService : IWorkspaceTopBarService, IDisposable
{
    private readonly NavigationManager _navigationManager;
    private readonly WorkspaceRepository _workspaceRepository;
    private readonly IHubContext<DesktopNotificationHub> _desktopHub;
    private readonly DesktopWorkspaceContextTracker _desktopContextTracker;
    private int _refreshGeneration;
    private int? _cachedWorkspaceId;
    private string? _workspaceDisplayName;

    public WorkspaceTopBarService(
        NavigationManager navigationManager,
        WorkspaceRepository workspaceRepository,
        IHubContext<DesktopNotificationHub> desktopHub,
        DesktopWorkspaceContextTracker desktopContextTracker)
    {
        _navigationManager = navigationManager;
        _workspaceRepository = workspaceRepository;
        _desktopHub = desktopHub;
        _desktopContextTracker = desktopContextTracker;
        _navigationManager.LocationChanged += OnLocationChanged;
    }

    public string? WorkspaceDisplayName => _workspaceDisplayName;

    public event Action? Changed;

    public void Dispose()
    {
        _navigationManager.LocationChanged -= OnLocationChanged;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        _ = ApplyNavigationAsync();
    }

    public Task EnsureSynchronizedAsync() => ApplyNavigationAsync();

    private async Task ApplyNavigationAsync()
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);

        var relativePath = _navigationManager.ToBaseRelativePath(_navigationManager.Uri);
        var path = relativePath.Split('?', '#')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        int? workspaceId = null;
        if (segments.Length >= 2
            && string.Equals(segments[0], "workspaces", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segments[1], out var parsedId))
        {
            workspaceId = parsedId;
        }

        if (workspaceId is null)
        {
            // Compare against the shared desktop tracker too, not just this instance's own cache:
            // a fresh circuit (e.g. after an F5 hard refresh or a WebView2 full navigation) starts
            // with both local fields null even though the singleton tracker may still hold a stale
            // workspace selection pushed by a previous circuit, which would otherwise leave the
            // desktop window title stuck on the old workspace name.
            var trackerHasStaleSelection = _desktopContextTracker.Current is { WorkspaceId: not null } or { WorkspaceName: not null };
            if (_workspaceDisplayName is not null || _cachedWorkspaceId is not null || trackerHasStaleSelection)
            {
                _cachedWorkspaceId = null;
                _workspaceDisplayName = null;
                if (generation == Volatile.Read(ref _refreshGeneration))
                {
                    Changed?.Invoke();
                    _ = PushToDesktopAsync(null, null);
                }
            }
            return;
        }

        if (_cachedWorkspaceId == workspaceId && _workspaceDisplayName is not null)
            return;

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId.Value).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _refreshGeneration))
            return;

        _cachedWorkspaceId = workspaceId;
        _workspaceDisplayName = workspace?.Name ?? "Workspace";
        Changed?.Invoke();
        _ = PushToDesktopAsync(workspaceId, _workspaceDisplayName);
    }

    private async Task PushToDesktopAsync(int? workspaceId, string? workspaceName)
    {
        var context = new WorkspaceContext(workspaceId, workspaceName);
        _desktopContextTracker.Current = context;

        try
        {
            await _desktopHub.Clients.All.SendAsync("WorkspaceChanged", context);
        }
        catch
        {
            // Best-effort - the desktop shell may not be connected (e.g. running in the browser, not desktop mode).
        }
    }
}
