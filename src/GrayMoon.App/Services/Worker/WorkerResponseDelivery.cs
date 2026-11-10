using System.Collections.Concurrent;
using GrayMoon.Abstractions.Worker;

namespace GrayMoon.App.Services.Worker;

/// <summary>Static registry for pending command responses. Worker calls ResponseCommand which completes the TCS.</summary>
public static class WorkerResponseDelivery
{
    private sealed class PendingRequest(
        TaskCompletionSource<WorkerCommandResponse> completion,
        Action<WorkerCommandStreamLine>? onStreamLine,
        string? connectionId)
    {
        public TaskCompletionSource<WorkerCommandResponse> Completion { get; } = completion;
        public Action<WorkerCommandStreamLine>? OnStreamLine { get; } = onStreamLine;

        /// <summary>Worker connection the command was sent to; null when the caller did not say.</summary>
        public string? ConnectionId { get; } = connectionId;

        /// <summary>Set once the entry is in <see cref="Pending"/>; default until then (Dispose on default is a no-op).</summary>
        public CancellationTokenRegistration Registration { get; set; }
    }

    private static readonly ConcurrentDictionary<string, PendingRequest> Pending = new();
    private static Action<string>? _onRequestCancelled;

    /// <summary>Registers a fire-and-forget notifier invoked when a pending wait is cancelled (e.g. job abort).</summary>
    public static void SetCancelNotifier(Action<string>? onRequestCancelled) =>
        _onRequestCancelled = onRequestCancelled;

    /// <summary>
    /// Waits until the worker completes the request or <paramref name="cancellationToken"/> is canceled.
    /// Callers are expected to pass a token that also carries an overall timeout (see
    /// <see cref="WorkerBridge.SendCommandAsync"/>) so this never blocks forever even if the Worker never
    /// responds.
    /// </summary>
    public static Task<WorkerCommandResponse> WaitAsync(
        string requestId,
        CancellationToken cancellationToken,
        Action<WorkerCommandStreamLine>? onStreamLine = null,
        string? connectionId = null)
    {
        var completion = new TaskCompletionSource<WorkerCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new PendingRequest(completion, onStreamLine, connectionId);

        // The entry goes in before the cancellation callback is registered. Register runs the callback
        // synchronously for an already-cancelled token; registering first would run it before the entry
        // exists, and the entry would then stay pending until the Worker disconnects.
        if (!Pending.TryAdd(requestId, entry))
            throw new InvalidOperationException($"Duplicate pending request ID '{requestId}'.");

        var registration = cancellationToken.Register(() =>
        {
            if (!Pending.TryRemove(new KeyValuePair<string, PendingRequest>(requestId, entry)))
                return;

            entry.Registration.Dispose();
            entry.Completion.TrySetCanceled(cancellationToken);
            _onRequestCancelled?.Invoke(requestId);
        });

        entry.Registration = registration;

        // The request finished (cancelled, completed or failed) while the callback was being registered:
        // whoever removed it disposed the default registration, so release the real one here.
        if (!Pending.TryGetValue(requestId, out var current) || !ReferenceEquals(current, entry))
            registration.Dispose();

        return completion.Task;
    }

    /// <summary>True while <paramref name="requestId"/> is waiting for its response.</summary>
    internal static bool IsPending(string requestId) => Pending.ContainsKey(requestId);

    /// <summary>Invoked from <see cref="Hubs.WorkerHub"/> when the worker pushes a streamed line for a pending request.</summary>
    public static void ReportStreamLine(string requestId, WorkerCommandStreamLine line)
    {
        if (!Pending.TryGetValue(requestId, out var pending))
            return;

        pending.OnStreamLine?.Invoke(line);
    }

    public static void Complete(string requestId, WorkerCommandResponse response)
    {
        if (!Pending.TryRemove(requestId, out var pending))
            return;

        pending.Registration.Dispose();
        pending.Completion.TrySetResult(response);
    }

    public static void Fail(string requestId, Exception exception)
    {
        if (!Pending.TryRemove(requestId, out var pending))
            return;

        pending.Registration.Dispose();
        pending.Completion.TrySetException(exception);
    }

    /// <summary>
    /// Fails the pending requests that were sent to <paramref name="connectionId"/> immediately with
    /// <paramref name="reason"/>. Called from <see cref="Hubs.WorkerHub"/> when that Worker connection drops, so
    /// its requests don't wait out their full timeout. Requests sent to another connection (a restarted Worker
    /// that connected before the old connection closed) are left alone.
    /// </summary>
    public static void FailForConnection(string connectionId, string reason)
    {
        foreach (var (requestId, pending) in Pending.ToArray())
        {
            if (!string.Equals(pending.ConnectionId, connectionId, StringComparison.Ordinal))
                continue;
            if (!Pending.TryRemove(new KeyValuePair<string, PendingRequest>(requestId, pending)))
                continue;

            pending.Registration.Dispose();
            pending.Completion.TrySetResult(new WorkerCommandResponse(false, null, reason));
        }
    }

    /// <summary>
    /// Fails every currently pending request immediately with <paramref name="reason"/>, whichever connection it
    /// was sent to. Used when the last Worker connection is gone, so nothing is left that could answer.
    /// </summary>
    public static void FailAll(string reason)
    {
        foreach (var requestId in Pending.Keys.ToArray())
        {
            if (!Pending.TryRemove(requestId, out var pending))
                continue;

            pending.Registration.Dispose();
            pending.Completion.TrySetResult(new WorkerCommandResponse(false, null, reason));
        }
    }
}

