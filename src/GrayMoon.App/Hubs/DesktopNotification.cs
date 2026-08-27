namespace GrayMoon.App.Hubs;

/// <summary>
/// A notification pushed from GrayMoon.App to GrayMoon.Desktop through the SignalR desktop hub.
/// Wire contract - must remain compatible with GrayMoon.Desktop/Models/DesktopNotification.cs.
/// </summary>
public sealed record DesktopNotification(
    string Id,
    string Title,
    string Message,
    DesktopNotificationSeverity Severity,
    string? NavigationPath,
    DateTimeOffset CreatedAt);

public enum DesktopNotificationSeverity
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3
}
