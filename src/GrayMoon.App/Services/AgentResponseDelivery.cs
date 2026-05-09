using System.Collections.Concurrent;
using GrayMoon.Abstractions.Agent;

namespace GrayMoon.App.Services;

/// <summary>Static registry for pending command responses. Agent calls ResponseCommand which completes the TCS.</summary>
public static class AgentResponseDelivery
{
    private sealed record PendingRequest(
        TaskCompletionSource<AgentCommandResponse> Completion,
        CancellationTokenRegistration Registration,
        CancellationTokenSource TimeoutCts,
        Action<AgentCommandStreamLine>? OnStreamLine);

    private static readonly ConcurrentDictionary<string, PendingRequest> Pending = new();

    public static Task<AgentCommandResponse> WaitAsync(
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<AgentCommandStreamLine>? onStreamLine = null)
    {
        var completion = new TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var registration = timeoutCts.Token.Register(() =>
        {
            if (!Pending.TryRemove(requestId, out var pending))
                return;

            pending.Registration.Dispose();
            pending.TimeoutCts.Dispose();

            if (cancellationToken.IsCancellationRequested)
            {
                pending.Completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                pending.Completion.TrySetException(new TimeoutException($"Timed out waiting for agent response for request '{requestId}' after {timeout}."));
            }

        });

        if (!Pending.TryAdd(requestId, new PendingRequest(completion, registration, timeoutCts, onStreamLine)))
        {
            registration.Dispose();
            timeoutCts.Dispose();
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
        pending.TimeoutCts.Dispose();
        pending.Completion.TrySetResult(response);
    }

    public static void Fail(string requestId, Exception exception)
    {
        if (!Pending.TryRemove(requestId, out var pending))
            return;

        pending.Registration.Dispose();
        pending.TimeoutCts.Dispose();
        pending.Completion.TrySetException(exception);
    }
}
