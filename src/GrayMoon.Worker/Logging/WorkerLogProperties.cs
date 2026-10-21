using GrayMoon.Abstractions.Logging;

namespace GrayMoon.Worker.Logging;

/// <summary>Serilog LogContext property names used by the worker. Prefer <see cref="GrayMoonLogProperties"/>.</summary>
public static class WorkerLogProperties
{
    /// <summary>Correlates log events with the active hub command <c>requestId</c> for overlay streaming.</summary>
    public const string RequestId = GrayMoonLogProperties.RequestId;

    /// <summary>Parent App operation id when carried on <c>RequestCommand</c>.</summary>
    public const string OperationId = GrayMoonLogProperties.OperationId;
}
