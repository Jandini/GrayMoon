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
/// </summary>
public sealed class DesktopNotificationHub(DesktopWorkspaceContextTracker workspaceContextTracker) : Hub
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
    }
}
