using System.Collections.Concurrent;
using GrayMoon.Abstractions.Agent;

namespace GrayMoon.App.Services;

/// <summary>Static registry for pending command responses. Agent calls ResponseCommand which completes the TCS.</summary>
public static class AgentResponseDelivery
{
    private sealed record PendingRequest(
        TaskCompletionSource<AgentCommandResponse> Completion,
        CancellationTokenRegistration Registration,
        Action<AgentCommandStreamLine>? OnStreamLine);

    private static readonly ConcurrentDictionary<string, PendingRequest> Pending = new();
    private static Action<string>? _onRequestCancelled;

    /// <summary>Registers a fire-and-forget notifier invoked when a pending wait is cancelled (e.g. job abort).</summary>
    public static void SetCancelNotifier(Action<string>? onRequestCancelled) =>
        _onRequestCancelled = onRequestCancelled;

    /// <summary>Waits until the agent completes the request or <paramref name="cancellationToken"/> is canceled (no time limit).</summary>
    public static Task<AgentCommandResponse> WaitAsync(
        string requestId,
        CancellationToken cancellationToken,
        Action<AgentCommandStreamLine>? onStreamLine = null)
    {
        var completion = new TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

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

    /// <summary>Invoked from <see cref="Hubs.AgentHub"/> when the agent pushes a streamed line for a pending request.</summary>
    public static void ReportStreamLine(string requestId, AgentCommandStreamLine line)
    {
        if (!Pending.TryGetValue(requestId, out var pending))
            return;

        pending.OnStreamLine?.Invoke(line);
    }

    public static void Complete(string requestId, AgentCommandResponse response)
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
}
