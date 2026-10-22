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
            .WithUrl(ResolveHubUri(navigationManager))
            .WithAutomaticReconnect()
            .Build();

    /// <summary>
    /// The hub is on the App's own host. A Desktop-launched App listens on 127.0.0.1 only, and localhost tries ::1
    /// first, which stalls the connect for seconds before falling back, so a loopback name is rewritten to the IPv4 address.
    /// </summary>
    internal static Uri ResolveHubUri(NavigationManager navigationManager)
    {
        var uri = navigationManager.ToAbsoluteUri(HubPath);
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(uri) { Host = "127.0.0.1" }.Uri
            : uri;
    }

    /// <summary>
    /// Starts <paramref name="hubConnection"/> and never throws: a failed first start (circuit closing, App busy) must not
    /// end the Blazor circuit, and live updates are an enhancement the page can do without. Returns whether it connected.
    /// </summary>
    public static async Task<bool> TryStartAsync(HubConnection hubConnection, ILogger logger)
    {
        try
        {
            await hubConnection.StartAsync();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Workspace-sync hub connection did not start");
            return false;
        }
    }

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
