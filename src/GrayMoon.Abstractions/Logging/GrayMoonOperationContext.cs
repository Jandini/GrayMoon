namespace GrayMoon.Abstractions.Logging;

/// <summary>
/// Ambient parent operation id (AsyncLocal). Callers push LogContext separately when they use Serilog.
/// Desktop native-bridge request ids are not this value.
/// </summary>
public static class GrayMoonOperationContext
{
    private static readonly AsyncLocal<string?> CurrentId = new();

    /// <summary>Operation id for the current async call chain, if any.</summary>
    public static string? Current => CurrentId.Value;

    /// <summary>
    /// Returns <paramref name="existing"/> when non-empty; otherwise the ambient id; otherwise a new full Guid N.
    /// </summary>
    public static string EnsureId(string? existing = null)
    {
        if (!string.IsNullOrEmpty(existing))
            return existing;
        return Current ?? NewId();
    }

    /// <summary>Allocates a new full Guid N (never truncated).</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Sets the ambient operation id for the current async call chain. Dispose to restore the previous value.
    /// </summary>
    public static IDisposable Begin(string? operationId = null)
    {
        var id = string.IsNullOrEmpty(operationId) ? NewId() : operationId;
        var previous = CurrentId.Value;
        CurrentId.Value = id;
        return new RestoreOnDispose(previous);
    }

    private sealed class RestoreOnDispose(string? previous) : IDisposable
    {
        public void Dispose() => CurrentId.Value = previous;
    }
}
