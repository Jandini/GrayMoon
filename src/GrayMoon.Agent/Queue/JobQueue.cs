using System.Threading.Channels;
using GrayMoon.Agent.Jobs;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Queue;

public sealed class JobQueue : IJobQueue
{
    private readonly Channel<JobEnvelope> _channel;

    public JobQueue(IOptions<AgentOptions> options)
    {
        var maxConcurrent = options.Value.MaxConcurrentCommands;
        var cap = Math.Max(maxConcurrent * 2, 64);
        _channel = Channel.CreateBounded<JobEnvelope>(new BoundedChannelOptions(cap)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(job, cancellationToken);

    public IAsyncEnumerable<JobEnvelope> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
