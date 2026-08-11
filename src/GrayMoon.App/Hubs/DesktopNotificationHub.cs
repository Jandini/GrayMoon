using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Hubs;

/// <summary>
/// Desktop notification hub — exists for IHubContext&lt;DesktopNotificationHub&gt;.
/// Only registered when GrayMoon.App is running in desktop mode (--desktop flag).
///
/// Push notifications to GrayMoon.Desktop by injecting IHubContext&lt;DesktopNotificationHub&gt;
/// and calling Clients.All.SendAsync("Notify", notification).
///
/// Wire contract: see GrayMoon.Desktop/Models/DesktopNotification.cs
/// Notification method name: "Notify"
///
/// Push the currently selected workspace by calling
/// Clients.All.SendAsync("WorkspaceChanged", context) with a <see cref="WorkspaceContext"/>.
/// Wire contract: see GrayMoon.Desktop/Models/WorkspaceContext.cs
/// Notification method name: "WorkspaceChanged"
///
/// Top bar visibility is a bool pushed as "TopBarVisibleChanged" (see DesktopTopBarState). It is
/// also caught up on every (re)connect, and GrayMoon.Desktop calls the "SetTopBarVisible" hub
/// method (below) when the user toggles it from the tray menu - the App's database is the single
/// source of truth, not a file on the Desktop side.
/// </summary>
public sealed class DesktopNotificationHub(
    DesktopWorkspaceContextTracker workspaceContextTracker,
    DesktopTopBarState topBarState,
    AppSettingRepository appSettingRepository) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();

        // Catch up a newly-connected (or reconnected) client with the last known workspace
        // selection, so the window title is never left stale after a connection gap.
        var current = workspaceContextTracker.Current;
        if (current is not null)
        {
            await Clients.Caller.SendAsync("WorkspaceChanged", current);
        }

        // Same idea for the top bar: the tray context menu label must always match reality,
        // even right after (re)connecting.
        await Clients.Caller.SendAsync("TopBarVisibleChanged", topBarState.IsVisible);
    }

    /// <summary>
    /// Called by GrayMoon.Desktop when the user toggles "Show/Hide Top Bar" from the tray menu.
    /// Persists the change to the App's database, updates the in-memory state every already-open
    /// Blazor circuit reads live, and broadcasts the confirmed value back to every connected
    /// client (including the caller) so the tray label only ever shows what was actually applied.
    /// </summary>
    public async Task SetTopBarVisible(bool visible)
    {
        await appSettingRepository.SetBoolAsync(AppSettingRepository.TopBarShowKey, visible);
        topBarState.SetVisible(visible);
        await Clients.All.SendAsync("TopBarVisibleChanged", visible);
    }
}
