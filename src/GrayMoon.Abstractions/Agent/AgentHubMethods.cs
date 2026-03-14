namespace GrayMoon.Abstractions.Agent;

/// <summary>
/// SignalR hub method names used between the app and the agent. Use these constants to avoid typos and drift.
/// </summary>
public static class AgentHubMethods
{
    /// <summary>App → Agent: send a command (requestId, command, args).</summary>
    public const string RequestCommand = "RequestCommand";

    /// <summary>Agent → App: command completed (requestId, payload).</summary>
    public const string ResponseCommand = "ResponseCommand";

    /// <summary>Agent → App: repository sync result from hooks (notification).</summary>
    public const string SyncCommand = "SyncCommand";

    /// <summary>Agent → App: report agent SemVer on connect/reconnect.</summary>
    public const string ReportSemVer = "ReportSemVer";

    /// <summary>Agent → App: report agent job queue status (total pending, per-workspace counts).</summary>
    public const string ReportQueueStatus = "ReportQueueStatus";
}
