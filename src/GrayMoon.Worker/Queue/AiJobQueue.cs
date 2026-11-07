using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Queue;

/// <summary>
/// Dedicated queue for the AI commands, sized by <see cref="WorkerOptions.MaxConcurrentAiCommands"/>. Like
/// <see cref="ReadJobQueue"/> and <see cref="DiffJobQueue"/> it does not broadcast <c>ReportQueueStatus</c>: that
/// telemetry drives the Worker badge activity frame and the per-Workspace pending count, and an AI generation shows its
/// own spinner on the AI control (or the commit overlay terminal) instead.
/// </summary>
public sealed class AiJobQueue(IOptions<WorkerOptions> options) : IAiJobQueue, IWorkerQueueTracker
{
    private readonly Channel<JobEnvelope> _channel = Channel.CreateBounded<JobEnvelope>(
        new BoundedChannelOptions(Math.Max(options.Value.MaxConcurrentAiCommands * 2, 8))
        {
            FullMode = BoundedChannelFullMode.Wait
        });

    public async ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default) =>
        await _channel.Writer.WriteAsync(job, cancellationToken);

    public async IAsyncEnumerable<JobEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var envelope in _channel.Reader.ReadAllAsync(cancellationToken))
            yield return envelope;
    }

    /// <inheritdoc />
    public void ReportJobCompleted(JobEnvelope envelope)
    {
        // Intentionally a no-op: AI jobs are not part of the worker-busy queue-status telemetry.
    }
}
