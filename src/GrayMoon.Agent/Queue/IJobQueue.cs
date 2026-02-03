using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Queue;

public interface IJobQueue
{
    ValueTask EnqueueAsync(QueuedJob job, CancellationToken cancellationToken = default);
    IAsyncEnumerable<QueuedJob> ReadAllAsync(CancellationToken cancellationToken = default);
}
