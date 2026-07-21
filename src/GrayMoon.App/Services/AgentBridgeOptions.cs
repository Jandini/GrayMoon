namespace GrayMoon.App.Services;

/// <summary>
/// Bounds how long the App waits for the Agent to respond to a single <see cref="IAgentBridge.SendCommandAsync"/>
/// call. Without this, <see cref="AgentResponseDelivery.WaitAsync"/> would wait forever if the Agent's
/// connection dropped mid-request without a clean disconnect, or if an Agent-side bug swallowed a
/// request. On timeout, <see cref="AgentBridge"/> notifies the Agent to cancel the request and returns a
/// normal failed <see cref="Abstractions.Agent.AgentCommandResponse"/> - matching the "fail fast, let the
/// user retry manually" behavior chosen for this feature - rather than throwing or hanging the caller.
/// </summary>
public sealed class AgentBridgeOptions
{
    public const string SectionName = "AgentBridge";

    /// <summary>
    /// Seconds to wait for a single Agent command round-trip. Default 240s - comfortably above the
    /// Agent's <c>NetworkTimeoutSeconds</c> (180s by default) for a single network git attempt, so the
    /// App does not give up mid-attempt while the Agent is still legitimately working.
    /// </summary>
    public int CommandTimeoutSeconds { get; init; } = 240;
}
