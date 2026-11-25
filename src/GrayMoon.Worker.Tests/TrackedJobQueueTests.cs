using GrayMoon.Worker;
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Queue;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The main pool takes a command the App sent before a hook sync. With one queue the 29 branch point commands of
/// Create Feature (40 repositories) waited up to 88 s behind the checkout-hook syncs of the new worktrees, each a
/// GitVersion run, while the user sat at "Verifying published packages..." (logs of 2026-11-25).
/// </summary>
public sealed class TrackedJobQueueTests
{
    private static TrackedJobQueue Create() =>
        new(Options.Create(new WorkerOptions()), new DisconnectedHubProvider(), NullLogger<TrackedJobQueue>.Instance);

    private static JobEnvelope HookSync(int repositoryId) =>
        JobEnvelope.Notify(new NotifySyncJob { RepositoryId = repositoryId, WorkspaceId = 1, RepositoryPath = "repo" });

    private static JobEnvelope Command(string requestId) =>
        JobEnvelope.Command(new CommandJob { RequestId = requestId, Command = "GetGitVersionAtBranchPoint", Request = new object() });

    [Fact]
    public async Task A_command_queued_behind_hook_syncs_is_read_first()
    {
        var queue = Create();
        var hookSyncs = new[] { HookSync(1), HookSync(2), HookSync(3) };
        foreach (var hookSync in hookSyncs)
            await queue.EnqueueAsync(hookSync);
        var firstCommand = Command("a");
        var secondCommand = Command("b");
        await queue.EnqueueAsync(firstCommand);
        await queue.EnqueueAsync(secondCommand);

        var read = await ReadAsync(queue, 5);

        // Commands first, in their order; then every hook sync, in its order. Nothing is lost.
        Assert.Equal([firstCommand, secondCommand, .. hookSyncs], read);
    }

    [Fact]
    public async Task A_reader_that_waits_on_an_empty_queue_gets_the_next_job_of_either_kind()
    {
        var queue = Create();
        var reading = ReadAsync(queue, 2);
        Assert.False(reading.IsCompleted);

        var hookSync = HookSync(1);
        var command = Command("a");
        await queue.EnqueueAsync(hookSync);
        await queue.EnqueueAsync(command);

        var read = await reading;
        Assert.Contains(hookSync, read);
        Assert.Contains(command, read);
    }

    [Fact]
    public async Task Several_readers_share_the_jobs_and_each_job_is_read_once()
    {
        var queue = Create();
        const int jobCount = 60;
        var readers = Enumerable.Range(0, 4).Select(_ => ReadAsync(queue, jobCount / 4)).ToArray();
        var queued = new List<JobEnvelope>();
        for (var i = 0; i < jobCount; i++)
        {
            var job = i % 2 == 0 ? HookSync(i) : Command(i.ToString());
            queued.Add(job);
            await queue.EnqueueAsync(job);
        }

        var read = (await Task.WhenAll(readers)).SelectMany(jobs => jobs).ToList();

        Assert.Equal(jobCount, read.Count);
        Assert.Equal(queued.ToHashSet(), read.ToHashSet());
    }

    private static async Task<List<JobEnvelope>> ReadAsync(TrackedJobQueue queue, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var read = new List<JobEnvelope>();
        await foreach (var envelope in queue.ReadAllAsync(cts.Token))
        {
            read.Add(envelope);
            if (read.Count == count)
                break;
        }

        return read;
    }
}
