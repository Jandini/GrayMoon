using GrayMoon.Application.Features;
using GrayMoon.App.Services.Jobs;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceOperationLockTests
{
    [Fact]
    public async Task Different_contexts_can_run_concurrently()
    {
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        var aEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var startedA = runner.TryStartContext(
            new WorkspaceFeatureContextId(1),
            workspaceId: 9,
            "push-a",
            WorkspaceJobKeys.ContextOverlayKey(9, 1),
            "Feature A...",
            async (_, _) =>
            {
                aEntered.SetResult();
                await release.Task;
            },
            out _);

        var startedB = runner.TryStartContext(
            new WorkspaceFeatureContextId(2),
            workspaceId: 9,
            "push-b",
            WorkspaceJobKeys.ContextOverlayKey(9, 2),
            "Feature B...",
            async (_, _) =>
            {
                bEntered.SetResult();
                await release.Task;
            },
            out _);

        Assert.True(startedA);
        Assert.True(startedB);
        await Task.WhenAll(aEntered.Task, bEntered.Task);
        release.SetResult();
        await WaitIdle(runner, 9);
    }

    [Fact]
    public async Task Same_context_blocks_second_mutation()
    {
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = false;

        Assert.True(runner.TryStartContext(
            new WorkspaceFeatureContextId(3),
            9,
            "push",
            WorkspaceJobKeys.ContextOverlayKey(9, 3),
            "A...",
            async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
            },
            out var first));

        await entered.Task;

        var startedSecond = runner.TryStartContext(
            new WorkspaceFeatureContextId(3),
            9,
            "commit",
            WorkspaceJobKeys.ContextOverlayKey(9, 3),
            "B...",
            (_, _) =>
            {
                secondRan = true;
                return Task.CompletedTask;
            },
            out var second);

        Assert.False(startedSecond);
        Assert.Same(first, second);
        Assert.False(secondRan);
        release.SetResult();
        await WaitIdle(runner, 9);
    }

    [Fact]
    public async Task Structural_blocks_context_and_context_blocks_structural()
    {
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        var structuralEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var structuralRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(runner.TryStartStructural(
            9,
            "create-feature",
            WorkspaceJobKeys.RepositoriesOverlayKey(9),
            "Creating...",
            async (_, _) =>
            {
                structuralEntered.SetResult();
                await structuralRelease.Task;
            },
            out _));

        await structuralEntered.Task;

        var contextStarted = runner.TryStartContext(
            new WorkspaceFeatureContextId(5),
            9,
            "push",
            WorkspaceJobKeys.ContextOverlayKey(9, 5),
            "Push...",
            (_, _) => Task.CompletedTask,
            out _);
        Assert.False(contextStarted);
        Assert.True(runner.IsWorkspaceStructurallyBusy(9));

        structuralRelease.SetResult();
        await WaitIdle(runner, 9);

        var contextEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contextRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(runner.TryStartContext(
            new WorkspaceFeatureContextId(5),
            9,
            "push",
            WorkspaceJobKeys.ContextOverlayKey(9, 5),
            "Push...",
            async (_, _) =>
            {
                contextEntered.SetResult();
                await contextRelease.Task;
            },
            out _));
        await contextEntered.Task;

        var structuralStarted = runner.TryStartStructural(
            9,
            "create-feature",
            WorkspaceJobKeys.RepositoriesOverlayKey(9),
            "Creating...",
            (_, _) => Task.CompletedTask,
            out _);
        Assert.False(structuralStarted);

        contextRelease.SetResult();
        await WaitIdle(runner, 9);
    }

    private static async Task WaitIdle(WorkspaceOperationRunner runner, int workspaceId)
    {
        for (var i = 0; i < 200; i++)
        {
            if (!runner.IsBusy(workspaceId))
                return;
            await Task.Delay(25);
        }

        Assert.Fail("Runner did not become idle.");
    }
}
