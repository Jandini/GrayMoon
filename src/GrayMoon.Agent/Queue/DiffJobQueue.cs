using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Queue;

/// <summary>
/// Dedicated queue for the diff command (GetGitFileDiff), sized independently from both the main command
/// queue and the status-scan read queue via <see cref="AgentOptions.MaxConcurrentDiffCommands"/>, so
/// opening a diff never queues behind a workspace status rescan (which can fan out many
/// GetGitChangeStatus calls) or any long-running write. Diff jobs are expected to be near-instant, so
/// pending-count tracking here is local bookkeeping only - like <see cref="ReadJobQueue"/>, it does not
/// broadcast <c>ReportQueueStatus</c> over SignalR, since that telemetry drives the "agent busy" spinner
/// and reads shouldn't visibly count toward it.
/// </summary>
public sealed class DiffJobQueue(IOptions<AgentOptions> options) : IDiffJobQueue, IAgentQueueTracker
{
    private readonly Channel<JobEnvelope> _channel = Channel.CreateBounded<JobEnvelope>(
        new BoundedChannelOptions(Math.Max(options.Value.MaxConcurrentDiffCommands * 2, 8))
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
        // Intentionally a no-op: diff jobs are near-instant and are not surfaced in the
        // agent-busy queue-status telemetry that TrackedJobQueue reports to the App.
    }
}
