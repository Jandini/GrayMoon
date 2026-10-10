namespace GrayMoon.App.Hubs;

/// <summary>
/// Sent once from GrayMoon.App to GrayMoon.Desktop when the Worker that connects at startup
/// is a different product version. Wire contract — must remain compatible with
/// GrayMoon.Desktop/Models/WorkerUpgradePrompt.cs.
/// </summary>
public sealed record WorkerUpgradePrompt(string? WorkerVersion, string? AppVersion)
{
    public const string ClientEventName = "WorkerUpgradeRequired";

    public const string UpgradeMethodName = "UpgradeWorker";
}
