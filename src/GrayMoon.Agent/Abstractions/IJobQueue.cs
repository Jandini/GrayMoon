using GrayMoon.Agent.Jobs;

namespace GrayMoon.Agent.Abstractions;

public interface IJobQueue
{
    ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default);
    IAsyncEnumerable<JobEnvelope> ReadAllAsync(CancellationToken cancellationToken = default);
}
