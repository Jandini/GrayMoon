namespace GrayMoon.Agent.Logging;

/// <summary>Serilog LogContext property names used by the agent.</summary>
public static class AgentLogProperties
{
    /// <summary>Correlates log events with the active hub command <c>requestId</c> for overlay streaming.</summary>
    public const string RequestId = "GrayMoonRequestId";
}
