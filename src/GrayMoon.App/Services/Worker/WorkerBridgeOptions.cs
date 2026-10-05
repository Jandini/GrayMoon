namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Bounds how long the App waits for the Worker to respond to a single <see cref="IWorkerBridge.SendCommandAsync"/>
/// call. Without this, <see cref="WorkerResponseDelivery.WaitAsync"/> would wait forever if the Worker's
/// connection dropped mid-request without a clean disconnect, or if a Worker-side bug swallowed a
/// request. On timeout, <see cref="WorkerBridge"/> notifies the Worker to cancel the request and returns a
/// normal failed <see cref="Abstractions.Worker.WorkerCommandResponse"/> - matching the "fail fast, let the
/// user retry manually" behavior chosen for this feature - rather than throwing or hanging the caller.
/// </summary>
public sealed class WorkerBridgeOptions
{
    public const string SectionName = "WorkerBridge";

    /// <summary>Configuration section name used before the Agent -> Worker rename; still honored.</summary>
    public const string LegacySectionName = "AgentBridge";

    /// <summary>
    /// Seconds to wait for a single Worker command round-trip. Default 240s - comfortably above the
    /// Worker's <c>NetworkTimeoutSeconds</c> (180s by default) for a single network git attempt, so the
    /// App does not give up mid-attempt while the Worker is still legitimately working.
    /// </summary>
    public int CommandTimeoutSeconds { get; init; } = 240;
}
