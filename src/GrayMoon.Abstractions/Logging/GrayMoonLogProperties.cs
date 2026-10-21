namespace GrayMoon.Abstractions.Logging;

/// <summary>
/// Serilog LogContext property names shared by App and Worker for cross-process diagnosis.
/// Keep <see cref="RequestId"/> equal to <c>GrayMoonRequestId</c> so the Worker overlay sink stays correct.
/// </summary>
public static class GrayMoonLogProperties
{
    /// <summary>Hub <c>requestId</c> for one App↔Worker command hop.</summary>
    public const string RequestId = "GrayMoonRequestId";

    /// <summary>Parent user/system operation that may issue many Worker commands (push, update, sync batch).</summary>
    public const string OperationId = "GrayMoonOperationId";
}
