using GrayMoon.Agent.Jobs;

namespace GrayMoon.Agent.Queue;

public interface IJobQueue
{
    ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default);
    IAsyncEnumerable<JobEnvelope> ReadAllAsync(CancellationToken cancellationToken = default);
}
