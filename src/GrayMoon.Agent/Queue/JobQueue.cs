using System.Threading.Channels;
using GrayMoon.Agent.Models;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Queue;

public sealed class JobQueue : IJobQueue
{
    private readonly Channel<QueuedJob> _channel;

    public JobQueue(IOptions<AgentOptions> options)
    {
        var maxConcurrent = options.Value.MaxConcurrentCommands;
        var cap = Math.Max(maxConcurrent * 2, 64);
        _channel = Channel.CreateBounded<QueuedJob>(new BoundedChannelOptions(cap)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public ValueTask EnqueueAsync(QueuedJob job, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(job, cancellationToken);

    public IAsyncEnumerable<QueuedJob> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
