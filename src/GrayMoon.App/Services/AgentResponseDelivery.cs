using System.Collections.Concurrent;
using GrayMoon.Abstractions.Agent;

namespace GrayMoon.App.Services;

/// <summary>Static registry for pending command responses. Agent calls ResponseCommand which completes the TCS.</summary>
public static class AgentResponseDelivery
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<AgentCommandResponse>> Pending = new();

    public static Task<AgentCommandResponse> WaitAsync(string requestId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<AgentCommandResponse>();
        Pending[requestId] = tcs;
        cancellationToken.Register(() =>
        {
            if (Pending.TryRemove(requestId, out var removed))
                removed.TrySetCanceled(cancellationToken);
        });
        return tcs.Task;
    }

    public static void Complete(string requestId, AgentCommandResponse response)
    {
        if (Pending.TryRemove(requestId, out var tcs))
            tcs.TrySetResult(response);
    }
}
