using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Queue;

/// <summary>
/// Wraps the agent job queue with tracking (total + per-workspace) and reports status to the app via SignalR.
/// Pending count is decremented when jobs actually complete (via ReportJobCompleted), so hook-triggered and in-progress work stay in the count until done.
/// </summary>
public sealed class TrackedJobQueue(
    IOptions<AgentOptions> options,
    IHubConnectionProvider hubProvider,
    ILogger<TrackedJobQueue> logger) : IJobQueue, IAgentQueueTracker
{
    private readonly Channel<JobEnvelope> _channel = Channel.CreateBounded<JobEnvelope>(
        new BoundedChannelOptions(Math.Max(options.Value.MaxConcurrentCommands * 2, 64))
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly ConcurrentDictionary<int, int> _byWorkspace = new();
    private int _totalPending;

    public async ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default)
    {
        var workspaceId = job.TryGetWorkspaceId();
        Increment(workspaceId);
        await _channel.Writer.WriteAsync(job, cancellationToken);
        _ = NotifyAppAsync();
    }

    public async IAsyncEnumerable<JobEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var envelope in _channel.Reader.ReadAllAsync(cancellationToken))
            yield return envelope;
    }

    /// <inheritdoc />
    public void ReportJobCompleted(JobEnvelope envelope)
    {
        var workspaceId = envelope.TryGetWorkspaceId();
        Decrement(workspaceId);
        _ = NotifyAppAsync();
    }

    private void Increment(int? workspaceId)
    {
        Interlocked.Increment(ref _totalPending);
        if (workspaceId.HasValue)
            _byWorkspace.AddOrUpdate(workspaceId.Value, 1, (_, c) => c + 1);
    }

    private void Decrement(int? workspaceId)
    {
        Interlocked.Decrement(ref _totalPending);
        if (workspaceId.HasValue)
        {
            _byWorkspace.AddOrUpdate(workspaceId.Value, 0, (_, c) => c > 0 ? c - 1 : 0);
            if (_byWorkspace.TryGetValue(workspaceId.Value, out var remaining) && remaining == 0)
                _byWorkspace.TryRemove(workspaceId.Value, out _);
        }
    }

    private async Task NotifyAppAsync()
    {
        var connection = hubProvider.Connection;
        if (connection?.State != HubConnectionState.Connected)
            return;

        var total = Interlocked.CompareExchange(ref _totalPending, 0, 0);
        var byWorkspace = new Dictionary<int, int>();
        foreach (var kv in _byWorkspace)
        {
            if (kv.Value > 0)
                byWorkspace[kv.Key] = kv.Value;
        }

        try
        {
            await connection.InvokeAsync(AgentHubMethods.ReportQueueStatus, total, byWorkspace, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to report queue status to app");
        }
    }
}
