namespace GrayMoon.App.Services.Worker;

/// <summary>Label and tooltip for the top-right worker badge.</summary>
internal static class WorkerStatusText
{
    public static string Label(bool selfUpdateInProgress, bool logonPasswordRequired, WorkerConnectionState state)
    {
        if (logonPasswordRequired)
            return "error";
        if (selfUpdateInProgress)
            return "installing";

        return state switch
        {
            // Queue activity keeps this label so the badge does not grow. The activity frame shows the work.
            WorkerConnectionState.Online => "online",
            WorkerConnectionState.Offline => "offline",
            WorkerConnectionState.Connecting => "connecting",
            WorkerConnectionState.VersionMismatch => "update",
            _ => "offline"
        };
    }

    public static string Title(
        bool selfUpdateInProgress,
        bool logonPasswordRequired,
        WorkerConnectionState state,
        string? workerSemVer,
        int pendingCount,
        bool unsecuredWorker)
    {
        if (logonPasswordRequired)
            return "The Windows password for the Worker service is no longer valid. Click to install the Worker again.";
        if (selfUpdateInProgress)
            return "Worker is installing an update.";

        return state switch
        {
            WorkerConnectionState.VersionMismatch => $"Worker version mismatch. Worker version: {workerSemVer ?? "unknown"}. Click to update.",
            WorkerConnectionState.Online => (pendingCount > 0 ? "Worker is running tasks" : "Worker connection status:  online")
                + (unsecuredWorker ? ". Reinstall the Worker to finish securing GrayMoon." : string.Empty),
            WorkerConnectionState.Offline => "Worker connection status:  offline",
            WorkerConnectionState.Connecting => "Worker connection status:  connecting",
            _ => "Worker connection status:  unknown"
        };
    }
}
