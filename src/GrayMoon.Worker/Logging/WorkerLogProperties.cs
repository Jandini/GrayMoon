namespace GrayMoon.Worker.Logging;

/// <summary>Serilog LogContext property names used by the worker.</summary>
public static class WorkerLogProperties
{
    /// <summary>Correlates log events with the active hub command <c>requestId</c> for overlay streaming.</summary>
    public const string RequestId = "GrayMoonRequestId";
}
