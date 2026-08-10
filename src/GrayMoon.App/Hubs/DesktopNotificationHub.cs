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
/// </summary>
public sealed class DesktopNotificationHub : Hub
{
    // Hub is empty — server pushes notifications; clients only subscribe.
}
