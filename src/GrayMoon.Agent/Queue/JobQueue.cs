using System.Threading.Channels;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Queue;

public sealed class JobQueue(IOptions<AgentOptions> options) : IJobQueue
{
    private readonly Channel<JobEnvelope> _channel = Channel.CreateBounded<JobEnvelope>(
        new BoundedChannelOptions(Math.Max(options.Value.MaxConcurrentCommands * 2, 64))
        {
            FullMode = BoundedChannelFullMode.Wait
        });

    public ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(job, cancellationToken);

    public IAsyncEnumerable<JobEnvelope> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
