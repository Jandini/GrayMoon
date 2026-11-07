using GrayMoon.Worker;
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Queue;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

public sealed class AiJobQueueTests
{
    private static AiJobQueue Create(int workers = 2) =>
        new(Options.Create(new WorkerOptions { MaxConcurrentAiCommands = workers }));

    private static JobEnvelope Job() =>
        JobEnvelope.Notify(new NotifySyncJob { RepositoryId = 1, WorkspaceId = 1, RepositoryPath = "repo" });

    [Fact]
    public async Task A_queued_job_is_read_back_in_order()
    {
        var queue = Create();
        var first = Job();
        var second = Job();
        await queue.EnqueueAsync(first);
        await queue.EnqueueAsync(second);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = new List<JobEnvelope>();
        await foreach (var envelope in queue.ReadAllAsync(cts.Token))
        {
            read.Add(envelope);
            if (read.Count == 2)
                break;
        }

        Assert.Same(first, read[0]);
        Assert.Same(second, read[1]);
    }

    [Fact]
    public void Completing_a_job_is_a_no_op_because_the_queue_is_untracked() =>
        Create().ReportJobCompleted(Job());
}
