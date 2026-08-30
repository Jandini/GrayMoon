namespace GrayMoon.App.Tests;

public sealed class WorkspaceOperationRunnerTests
{
    [Fact]
    public async Task TryStart_second_caller_gets_existing_run_and_does_not_start_work()
    {
        var runner = new WorkspaceOperationRunner();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWorkRan = false;

        var startedFirst = runner.TryStart(
            7,
            "workspace",
            WorkspaceJobKeys.WorkspaceFamily,
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
            WorkspaceJobKeys.WorkspaceFamily,
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
    public void WorkspaceJobKeys_classifies_overlay_families()
    {
        Assert.True(WorkspaceJobKeys.IsMutationKey("/workspaces/3/repositories", out var reposId));
        Assert.Equal(3, reposId);
        Assert.Equal(WorkspaceJobKeys.WorkspaceFamily, WorkspaceJobKeys.OverlayFamily("/workspaces/3/repositories"));
        Assert.Equal(WorkspaceJobKeys.WorkspaceFamily, WorkspaceJobKeys.OverlayFamily("/workspaces/3"));

        Assert.True(WorkspaceJobKeys.IsMutationKey("/workspaces/3/changes", out var changesId));
        Assert.Equal(3, changesId);
        Assert.Equal(WorkspaceJobKeys.ChangesFamily, WorkspaceJobKeys.OverlayFamily("/workspaces/3/changes"));

        Assert.False(WorkspaceJobKeys.IsMutationKey("/workspaces/3/changes:scan", out _));
        Assert.False(WorkspaceJobKeys.IsMutationKey("/connectors", out _));
    }
}

public sealed class BackgroundJobServiceWorkspaceLockTests
{
    [Fact]
    public async Task StartJob_on_second_workspace_key_does_not_run_a_second_body()
    {
        var runner = new WorkspaceOperationRunner();
        using var service = new BackgroundJobService(runner);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWorkRan = false;

        service.StartJob("/workspaces/4/repositories", "Pushing...", async (_, _) =>
        {
            firstEntered.SetResult();
            await firstRelease.Task;
        });

        await firstEntered.Task;
        service.StartJob("/workspaces/4", "Updating...", (_, _) =>
        {
            secondWorkRan = true;
            return Task.CompletedTask;
        });

        Assert.False(secondWorkRan);
        Assert.True(service.IsRunning("/workspaces/4/repositories"));
        Assert.True(service.IsRunning("/workspaces/4"));
        Assert.True(runner.IsBusy(4));

        var attached = service.GetJob("/workspaces/4/repositories");
        Assert.NotNull(attached);
        Assert.Equal(BackgroundJobState.Running, attached.State);

        var changesOverlay = service.GetJob("/workspaces/4/changes");
        Assert.True(changesOverlay is null || changesOverlay.State != BackgroundJobState.Running);

        firstRelease.SetResult();
        await WorkspaceJobTestWait.WaitUntilAsync(() => !runner.IsBusy(4));
    }

    [Fact]
    public async Task Scan_key_does_not_take_the_workspace_lock()
    {
        var runner = new WorkspaceOperationRunner();
        using var service = new BackgroundJobService(runner);
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

        service.StartJob("/workspaces/8/repositories", "Pushing...", (_, _) =>
        {
            mutationRan = true;
            return Task.CompletedTask;
        });

        await WorkspaceJobTestWait.WaitUntilAsync(() => mutationRan);
        scanRelease.SetResult();
        await WorkspaceJobTestWait.WaitUntilAsync(() => !service.IsRunning("/workspaces/8/changes:scan"));
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
