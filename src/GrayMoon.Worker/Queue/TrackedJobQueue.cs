using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Queue;

/// <summary>
/// Wraps the worker job queue with tracking (total + per-workspace) and reports status to the app via SignalR.
/// Pending count is decremented when jobs actually complete (via ReportJobCompleted), so hook-triggered and in-progress work stay in the count until done.
/// <para>
/// Two queues, one set of workers: a command the App sent is always taken before a hook sync. The App (and
/// usually a user at an overlay) waits for a command; nobody waits for a hook sync. With one queue the 29 branch
/// point commands of Create Feature waited up to 88 s behind the 40 checkout-hook syncs of the new worktrees
/// (logs of 2026-11-25). A hook sync that is already running is not interrupted, and hook syncs keep their order.
/// </para>
/// </summary>
public sealed class TrackedJobQueue(
    IOptions<WorkerOptions> options,
    IHubConnectionProvider hubProvider,
    ILogger<TrackedJobQueue> logger) : IJobQueue, IWorkerQueueTracker
{
    private readonly Channel<JobEnvelope> _commands = CreateChannel(options.Value);
    private readonly Channel<JobEnvelope> _hookSyncs = CreateChannel(options.Value);

    /// <summary>One permit per queued job of either channel, released after the job is written.</summary>
    private readonly SemaphoreSlim _queued = new(0);
    private readonly ConcurrentDictionary<int, int> _byWorkspace = new();
    private int _totalPending;

    public async ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default)
    {
        var workspaceId = job.TryGetWorkspaceId();
        var channel = job.Kind == JobKind.Notify ? _hookSyncs : _commands;
        await channel.Writer.WriteAsync(job, cancellationToken);
        _queued.Release();
        Increment(workspaceId);
        _ = NotifyAppAsync();
    }

    public async IAsyncEnumerable<JobEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // A permit means a job is queued that no other reader has claimed: every reader takes one permit and
            // then one job, so one of the two reads below finds it.
            await _queued.WaitAsync(cancellationToken);
            if (_commands.Reader.TryRead(out var envelope) || _hookSyncs.Reader.TryRead(out envelope))
                yield return envelope;
            else
                throw new InvalidOperationException("The Worker job queue counted a job that is in neither queue.");
        }
    }

    private static Channel<JobEnvelope> CreateChannel(WorkerOptions options) =>
        Channel.CreateBounded<JobEnvelope>(
            new BoundedChannelOptions(Math.Max(options.MaxConcurrentCommands * 2, 64))
            {
                FullMode = BoundedChannelFullMode.Wait
            });

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
            await connection.InvokeAsync(WorkerHubMethods.ReportQueueStatus, total, byWorkspace, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to report queue status to app");
        }
    }
}
