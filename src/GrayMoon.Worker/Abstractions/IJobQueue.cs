using GrayMoon.Worker.Jobs;

namespace GrayMoon.Worker.Abstractions;

public interface IJobQueue
{
    ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default);
    IAsyncEnumerable<JobEnvelope> ReadAllAsync(CancellationToken cancellationToken = default);
}
