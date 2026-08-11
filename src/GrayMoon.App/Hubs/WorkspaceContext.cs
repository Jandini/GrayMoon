namespace GrayMoon.App.Hubs;

/// <summary>
/// Current workspace selection pushed from GrayMoon.App to GrayMoon.Desktop through the SignalR desktop hub.
/// Wire contract - must remain compatible with GrayMoon.Desktop/Models/WorkspaceContext.cs.
/// </summary>
public sealed record WorkspaceContext(int? WorkspaceId, string? WorkspaceName);
