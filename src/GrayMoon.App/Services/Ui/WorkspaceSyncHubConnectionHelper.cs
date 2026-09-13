using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Builds and tears down the browser-side connection to the workspace-sync SignalR hub
/// (<c>/hubs/workspace-sync</c>). Every page/component that reacts to workspace-sync broadcasts
/// (<c>WorkspaceSynced</c>, <c>RepositorySynced</c>, <c>GitChangesUpdated</c>, <c>RepositoryError</c>, etc.)
/// uses the same hub URL and reconnect policy - only the specific event subscriptions and handler logic
/// differ per caller, and those stay at each call site rather than being unified here.
/// </summary>
public static class WorkspaceSyncHubConnectionHelper
{
    private const string HubPath = "/hubs/workspace-sync";

    /// <summary>
    /// Builds (but does not start) a connection to the workspace-sync hub with automatic reconnect.
    /// Callers register their own <c>.On(...)</c> handlers and then call <see cref="HubConnection.StartAsync"/>.
    /// </summary>
    public static HubConnection Create(NavigationManager navigationManager) =>
        new HubConnectionBuilder()
            .WithUrl(navigationManager.ToAbsoluteUri(HubPath))
            .WithAutomaticReconnect()
            .Build();

    /// <summary>
    /// Stops and disposes <paramref name="hubConnection"/> without awaiting completion - the fire-and-forget
    /// teardown used by synchronous <c>Dispose()</c> methods that cannot await. Not used by components whose
    /// teardown is itself async (e.g. an <see cref="IAsyncDisposable"/> that awaits disposal directly).
    /// </summary>
    public static void DisposeFireAndForget(HubConnection? hubConnection)
    {
        _ = hubConnection?.StopAsync();
        _ = hubConnection?.DisposeAsync().AsTask();
    }
}
