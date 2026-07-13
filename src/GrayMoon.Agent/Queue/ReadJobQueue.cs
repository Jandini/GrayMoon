using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Queue;

/// <summary>
/// Dedicated queue for read-only commands (GetGitFileDiff, GetGitChangeStatus), sized independently
/// from the main command queue via <see cref="AgentOptions.MaxConcurrentReadCommands"/> so reads stay
/// responsive even when the main pool is saturated by long-running writes. Read jobs are expected to
/// be near-instant, so pending-count tracking here is local bookkeeping only - unlike
/// <see cref="TrackedJobQueue"/>, it does not broadcast <c>ReportQueueStatus</c> over SignalR, since
/// that telemetry drives the "agent busy" spinner and reads shouldn't visibly count toward it.
/// </summary>
public sealed class ReadJobQueue(IOptions<AgentOptions> options) : IReadJobQueue, IAgentQueueTracker
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
        // agent-busy queue-status telemetry that TrackedJobQueue reports to the App.
    }
}
