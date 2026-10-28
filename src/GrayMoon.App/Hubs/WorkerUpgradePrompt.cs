namespace GrayMoon.App.Hubs;

/// <summary>
/// Sent once from GrayMoon.App to GrayMoon.Desktop when the Worker that connects at startup
/// is a different product version. Wire contract — must remain compatible with
/// GrayMoon.Desktop/Models/WorkerUpgradePrompt.cs.
/// <see cref="AutoUpdate"/> is true when the user chose to always update the Worker: Desktop then
/// starts the update without showing the dialog.
/// </summary>
public sealed record WorkerUpgradePrompt(string? WorkerVersion, string? AppVersion, bool AutoUpdate = false)
{
    public const string ClientEventName = "WorkerUpgradeRequired";

    public const string UpgradeMethodName = "UpgradeWorker";

    public const string SetAutoUpdateMethodName = "SetWorkerAutoUpdate";
}
