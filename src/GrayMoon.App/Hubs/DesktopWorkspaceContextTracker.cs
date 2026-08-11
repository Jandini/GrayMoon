namespace GrayMoon.App.Hubs;

/// <summary>
/// Holds the last workspace selection pushed to GrayMoon.Desktop, so a client that connects (or
/// reconnects) to <see cref="DesktopNotificationHub"/> after the selection changed can be caught up
/// immediately instead of showing a stale window title.
/// </summary>
public sealed class DesktopWorkspaceContextTracker
{
    public WorkspaceContext? Current { get; set; }
}
