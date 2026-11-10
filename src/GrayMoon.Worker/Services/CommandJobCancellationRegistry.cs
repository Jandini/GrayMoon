using System.Collections.Concurrent;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Tracks per-request cancellation for command jobs. CancelCommand marks a requestId cancelled
/// even before the job starts; workers skip or abort using the linked token.
/// </summary>
public sealed class CommandJobCancellationRegistry
{
    /// <summary>
    /// How many cancellations for requests that are not registered are remembered. A CancelCommand that arrives after
    /// its job finished (the usual case) is never followed by a RequestCommand, so without a bound each one would
    /// stay for the life of the process.
    /// </summary>
    internal const int MaxPendingCancellations = 256;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _byRequestId = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<KeyValuePair<string, CancellationTokenSource>> _pendingCancellations = new();

    /// <summary>Number of tracked requests, including remembered cancellations.</summary>
    internal int Count => _byRequestId.Count;

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
    /// Marks <paramref name="requestId"/> as cancelled. Safe to call before or after Register. A cancellation for a
    /// request that is not registered is remembered for a RequestCommand still on its way, up to
    /// <see cref="MaxPendingCancellations"/>; beyond that the oldest is forgotten.
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
            return;
        }

        _pendingCancellations.Enqueue(new KeyValuePair<string, CancellationTokenSource>(requestId, cts));
        TrimPendingCancellations();
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
            TryDispose(cts);
    }

    public CancellationToken GetTokenOrNone(string requestId)
    {
        if (!string.IsNullOrEmpty(requestId) && _byRequestId.TryGetValue(requestId, out var cts))
            return cts.Token;
        return CancellationToken.None;
    }

    /// <summary>
    /// Forgets the oldest remembered cancellations beyond the bound. Removal is by key and value, so an entry that was
    /// already unregistered (its request arrived and finished), or whose id has since been registered afresh, is left alone.
    /// </summary>
    private void TrimPendingCancellations()
    {
        while (_pendingCancellations.Count > MaxPendingCancellations && _pendingCancellations.TryDequeue(out var oldest))
        {
            if (_byRequestId.TryRemove(oldest))
                TryDispose(oldest.Value);
        }
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

    private static void TryDispose(CancellationTokenSource cts)
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
