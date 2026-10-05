using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Queue;

/// <summary>
/// Dedicated queue for read-only commands (GetGitFileDiff, GetGitChangeStatus), sized independently
/// from the main command queue via <see cref="WorkerOptions.MaxConcurrentReadCommands"/> so reads stay
/// responsive even when the main pool is saturated by long-running writes. Read jobs are expected to
/// be near-instant, so pending-count tracking here is local bookkeeping only - unlike
/// <see cref="TrackedJobQueue"/>, it does not broadcast <c>ReportQueueStatus</c> over SignalR, since
/// that telemetry drives the "worker busy" spinner and reads shouldn't visibly count toward it.
/// </summary>
public sealed class ReadJobQueue(IOptions<WorkerOptions> options) : IReadJobQueue, IWorkerQueueTracker
{
    private readonly Channel<JobEnvelope> _channel = Channel.CreateBounded<JobEnvelope>(
        new BoundedChannelOptions(Math.Max(options.Value.MaxConcurrentReadCommands * 2, 8))
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
        // Intentionally a no-op: read jobs are near-instant and are not surfaced in the
        // worker-busy queue-status telemetry that TrackedJobQueue reports to the App.
    }
}
