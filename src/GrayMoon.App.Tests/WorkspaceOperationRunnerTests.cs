using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceOperationRunnerTests
{
    [Fact]
    public async Task TryStart_second_caller_gets_existing_run_and_does_not_start_work()
    {
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWorkRan = false;

        var startedFirst = runner.TryStart(
            7,
            "workspace",
            WorkspaceJobKeys.RepositoriesOverlayKey(7),
            "Pushing...",
            async (_, _) =>
            {
                firstEntered.SetResult();
                await firstRelease.Task;
            },
            out var first);

        Assert.True(startedFirst);
        await firstEntered.Task;

        var startedSecond = runner.TryStart(
            7,
            "workspace",
            WorkspaceJobKeys.RepositoriesOverlayKey(7),
            "Updating...",
            (_, _) =>
            {
                secondWorkRan = true;
                return Task.CompletedTask;
            },
            out var second);

        Assert.False(startedSecond);
        Assert.Same(first, second);
        Assert.False(secondWorkRan);
        Assert.True(runner.IsBusy(7));

        firstRelease.SetResult();
        await WorkspaceJobTestWait.WaitUntilAsync(() => !runner.IsBusy(7));
    }

    [Fact]
    public async Task TryStart_logs_error_when_work_faults()
    {
        var logger = new RecordingLogger<WorkspaceOperationRunner>();
        var runner = new WorkspaceOperationRunner(logger);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        runner.TryStart(
            11,
            "workspace",
            WorkspaceJobKeys.RepositoriesOverlayKey(11),
            "Pushing...",
            (_, _) =>
            {
                entered.SetResult();
                throw new InvalidOperationException("wait restore failed");
            },
            out _);

        await entered.Task;
        await WorkspaceJobTestWait.WaitUntilAsync(() => !runner.IsBusy(11));

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error
            && e.Exception is InvalidOperationException
            && e.Message.Contains("wait restore failed", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkspaceJobKeys_classifies_mutation_keys()
    {
        Assert.True(WorkspaceJobKeys.IsMutationKey("/workspaces/3", out var reposId));
        Assert.Equal(3, reposId);
        Assert.Equal("/workspaces/3", WorkspaceJobKeys.RepositoriesOverlayKey(3));
        Assert.Equal("/workspaces/3", WorkspaceJobKeys.NormalizeOverlayKey("/workspaces/3/"));

        Assert.True(WorkspaceJobKeys.IsMutationKey("/workspaces/3/changes", out var changesId));
        Assert.Equal(3, changesId);

        Assert.False(WorkspaceJobKeys.IsMutationKey("/workspaces/3/changes:scan", out _));
        Assert.False(WorkspaceJobKeys.IsMutationKey("/connectors", out _));
    }
}

public sealed class BackgroundJobServiceWorkspaceLockTests
{
    [Fact]
    public async Task StartJob_on_second_workspace_key_does_not_run_a_second_body()
    {
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        using var service = new BackgroundJobService(runner, NullLogger<BackgroundJobService>.Instance);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWorkRan = false;

        service.StartJob("/workspaces/4", "Pushing...", async (_, _) =>
        {
            firstEntered.SetResult();
            await firstRelease.Task;
        });

        await firstEntered.Task;
        service.StartJob("/workspaces/4/files", "Updating...", (_, _) =>
        {
            secondWorkRan = true;
            return Task.CompletedTask;
        });

        Assert.False(secondWorkRan);
        Assert.True(service.IsRunning("/workspaces/4"));
        Assert.True(service.IsRunning("/workspaces/4/files"));
        Assert.True(runner.IsBusy(4));

        var attached = service.GetJob("/workspaces/4");
        Assert.NotNull(attached);
        Assert.Equal(BackgroundJobState.Running, attached.State);

        AssertNoRunningOverlay(service, "/workspaces/4/files");
        AssertNoRunningOverlay(service, "/workspaces/4/actions");
        AssertNoRunningOverlay(service, "/workspaces/4/packages");
        AssertNoRunningOverlay(service, "/workspaces/4/projects");
        AssertNoRunningOverlay(service, "/workspaces/4/dependencies");
        AssertNoRunningOverlay(service, "/workspaces/4/changes");

        firstRelease.SetResult();
        await WorkspaceJobTestWait.WaitUntilAsync(() => !runner.IsBusy(4));
    }

    [Fact]
    public async Task GetJob_attaches_only_on_originating_page_path()
    {
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        using var service = new BackgroundJobService(runner, NullLogger<BackgroundJobService>.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        service.StartJob("/workspaces/5", "Creating branches...", async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        });

        await entered.Task;

        var origin = service.GetJob("/workspaces/5");
        Assert.NotNull(origin);
        Assert.Equal(BackgroundJobState.Running, origin.State);

        AssertNoRunningOverlay(service, "/workspaces/5/files");
        AssertNoRunningOverlay(service, "/workspaces/5/actions");
        AssertNoRunningOverlay(service, "/workspaces/5/packages");
        AssertNoRunningOverlay(service, "/workspaces/5/projects");
        AssertNoRunningOverlay(service, "/workspaces/5/dependencies");
        AssertNoRunningOverlay(service, "/workspaces/5/changes");

        release.SetResult();
        await WorkspaceJobTestWait.WaitUntilAsync(() => !runner.IsBusy(5));
    }

    [Fact]
    public async Task Scan_key_does_not_take_the_workspace_lock()
    {
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        using var service = new BackgroundJobService(runner, NullLogger<BackgroundJobService>.Instance);
        var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationRan = false;

        service.StartJob("/workspaces/8/changes:scan", "Refreshing...", async (_, _) =>
        {
            scanEntered.SetResult();
            await scanRelease.Task;
        });

        await scanEntered.Task;
        Assert.False(runner.IsBusy(8));

        service.StartJob("/workspaces/8", "Pushing...", (_, _) =>
        {
            mutationRan = true;
            return Task.CompletedTask;
        });

        await WorkspaceJobTestWait.WaitUntilAsync(() => mutationRan);
        scanRelease.SetResult();
        await WorkspaceJobTestWait.WaitUntilAsync(() => !service.IsRunning("/workspaces/8/changes:scan"));
    }

    [Fact]
    public async Task StartJob_logs_error_when_circuit_job_faults()
    {
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        var logger = new RecordingLogger<BackgroundJobService>();
        using var service = new BackgroundJobService(runner, logger);

        service.StartJob("/workspaces/8/changes:scan", "Refreshing...", (_, _) =>
            throw new InvalidOperationException("scan boom"));

        await WorkspaceJobTestWait.WaitUntilAsync(() =>
            service.GetJob("/workspaces/8/changes:scan")?.State == BackgroundJobState.Faulted);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error
            && e.Exception is InvalidOperationException
            && e.Message.Contains("scan boom", StringComparison.Ordinal));
    }

    private static void AssertNoRunningOverlay(BackgroundJobService service, string jobKey)
    {
        var overlay = service.GetJob(jobKey);
        Assert.True(overlay is null || overlay.State != BackgroundJobState.Running, jobKey);
    }
}

file static class WorkspaceJobTestWait
{
    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting for job state.");
            await Task.Delay(10);
        }
    }
}
