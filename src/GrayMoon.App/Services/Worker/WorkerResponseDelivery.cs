using System.Collections.Concurrent;
using GrayMoon.Abstractions.Worker;

namespace GrayMoon.App.Services.Worker;

/// <summary>Static registry for pending command responses. Worker calls ResponseCommand which completes the TCS.</summary>
public static class WorkerResponseDelivery
{
    private sealed record PendingRequest(
        TaskCompletionSource<WorkerCommandResponse> Completion,
        CancellationTokenRegistration Registration,
        Action<WorkerCommandStreamLine>? OnStreamLine);

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
        Action<WorkerCommandStreamLine>? onStreamLine = null)
    {
        var completion = new TaskCompletionSource<WorkerCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        var registration = cancellationToken.Register(() =>
        {
            if (!Pending.TryRemove(requestId, out var pending))
                return;

            pending.Registration.Dispose();
            pending.Completion.TrySetCanceled(cancellationToken);
            _onRequestCancelled?.Invoke(requestId);
        });

        if (!Pending.TryAdd(requestId, new PendingRequest(completion, registration, onStreamLine)))
        {
            registration.Dispose();
            throw new InvalidOperationException($"Duplicate pending request ID '{requestId}'.");
        }

        return completion.Task;
    }

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
    /// Fails every currently pending request immediately with <paramref name="reason"/>. Called from
    /// <see cref="Hubs.WorkerHub"/> when the Worker's connection drops so requests in flight at that moment
    /// don't wait out their full timeout - matches the existing single-worker-connection model, so no
    /// per-connection filtering is needed here.
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

