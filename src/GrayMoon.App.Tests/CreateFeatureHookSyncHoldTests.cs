using GrayMoon.Abstractions.Worker;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Create Feature holds the Worker's deferred Checkout syncs from before the first worktree until its version step has
/// ended. They were released 500 ms after the last worktree and ran GitVersion for every new worktree beside the
/// GitVersion runs of that step (logs of 2026-11-25).
/// </summary>
public sealed class CreateFeatureHookSyncHoldTests
{
    [Fact]
    public async Task Checkout_syncs_are_held_before_the_first_worktree_and_released_after_the_last()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await RespondToHeadCommits(ctx);
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:wt" });
        ctx.WorkerBridge.Respond(WorkerHubMethods.HoldWorktreeHookSyncs, new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feature/held", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.True(result.Success, result.Error);
        var calls = ctx.WorkerBridge.Calls
            .Where(c => c.Command is WorkerHubMethods.HoldWorktreeHookSyncs or WorkerHubMethods.CreateGitWorktree)
            .Select(c => c.Command == WorkerHubMethods.CreateGitWorktree ? "create" : Arg(c.Args, "hold") is true ? "hold" : "release")
            .ToList();
        Assert.Equal("hold", calls[0]);
        Assert.Equal("release", calls[^1]);
        Assert.True(calls.LastIndexOf("create") < calls.IndexOf("release"));
        var key = $"create-feature-{result.ContextId!.Value.Value}";
        Assert.All(
            ctx.WorkerBridge.Calls.Where(c => c.Command == WorkerHubMethods.HoldWorktreeHookSyncs),
            c => Assert.Equal(key, Arg(c.Args, "key")));
    }

    [Fact]
    public async Task The_hold_is_released_when_worktree_creation_throws()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await RespondToHeadCommits(ctx);
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, _ => throw new InvalidOperationException("worker gone"));
        ctx.WorkerBridge.Respond(WorkerHubMethods.HoldWorktreeHookSyncs, new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feature/failed", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(result.Success);
        var last = ctx.WorkerBridge.Calls.Last(c => c.Command == WorkerHubMethods.HoldWorktreeHookSyncs);
        Assert.Equal(false, Arg(last.Args, "hold"));
    }

    private static object? Arg(object args, string name) => args.GetType().GetProperty(name)?.GetValue(args);

    private static Task RespondToHeadCommits(SyncStateTestContext ctx)
    {
        ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "abc123def456abc123def456abc123def456abc1",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "develop",
            },
        });
        return ctx.StoreHeadSnapshotAsCurrentStateAsync();
    }
}
