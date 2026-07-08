using System.Collections.Concurrent;

namespace GrayMoon.Agent.Services;

/// <summary>
/// Tracks per-request cancellation for command jobs. CancelCommand marks a requestId cancelled
/// even before the job starts; workers skip or abort using the linked token.
/// </summary>
public sealed class CommandJobCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _byRequestId = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a CTS linked to <paramref name="hostStoppingToken"/> for this request.
    /// If CancelCommand already arrived, returns the pre-cancelled CTS.
    /// </summary>
    public CancellationTokenSource Register(string requestId, CancellationToken hostStoppingToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);

        if (_byRequestId.TryGetValue(requestId, out var existing))
            return existing;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(hostStoppingToken);
        if (_byRequestId.TryAdd(requestId, cts))
            return cts;

        cts.Dispose();
        return _byRequestId.TryGetValue(requestId, out existing)
            ? existing
            : CancellationTokenSource.CreateLinkedTokenSource(hostStoppingToken);
    }

    /// <summary>
    /// Marks <paramref name="requestId"/> as cancelled. Safe to call before or after Register.
    /// </summary>
    public void Cancel(string requestId)
    {
        if (string.IsNullOrEmpty(requestId))
            return;

        if (_byRequestId.TryGetValue(requestId, out var existing))
        {
            TryCancel(existing);
            return;
        }

        var cts = new CancellationTokenSource();
        cts.Cancel();
        if (!_byRequestId.TryAdd(requestId, cts))
        {
            cts.Dispose();
            if (_byRequestId.TryGetValue(requestId, out existing))
                TryCancel(existing);
        }
    }

    /// <summary>True if this request was cancelled.</summary>
    public bool IsCancelled(string requestId) =>
        !string.IsNullOrEmpty(requestId)
        && _byRequestId.TryGetValue(requestId, out var cts)
        && cts.IsCancellationRequested;

    /// <summary>Removes and disposes the CTS for a completed job.</summary>
    public void Unregister(string requestId)
    {
        if (string.IsNullOrEmpty(requestId))
            return;

        if (_byRequestId.TryRemove(requestId, out var cts))
        {
            try
            {
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public CancellationToken GetTokenOrNone(string requestId)
    {
        if (!string.IsNullOrEmpty(requestId) && _byRequestId.TryGetValue(requestId, out var cts))
            return cts.Token;
        return CancellationToken.None;
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            if (!cts.IsCancellationRequested)
                cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
