using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Restore's initial sync calls WorkspaceGitService.SyncAsync. These tests prove that fan-out admits
/// more than one SyncRepository at a time, and that MaxParallelOperations of 1 keeps it to one.
/// </summary>
public sealed class SyncRepositoryFanOutTests
{
    [Fact]
    public async Task Sync_admits_multiple_repositories_before_any_returns()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await AddRepositoryAsync(ctx, "second-repo");
        var active = 0;
        var maxActive = new int[1];
        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.WorkerBridge.RespondAsync("SyncRepository", async (_, _) =>
        {
            var now = Interlocked.Increment(ref active);
            Observe(maxActive, now);
            if (Interlocked.Increment(ref entered) == 2)
                allEntered.TrySetResult();
            try
            {
                await release.Task;
                return new WorkerCommandResponse(true, SyncResponse(), null);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var run = git.SyncAsync(ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync());
        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, Volatile.Read(ref maxActive[0]));
        release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, ctx.WorkerBridge.Calls.Count(call => call.Command == "SyncRepository"));
    }

    [Fact]
    public async Task Sync_with_parallelism_one_never_overlaps_two_repositories()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync(configureServices: services =>
        {
            services.Configure<WorkspaceOptions>(options => options.MaxParallelOperations = 1);
        });
        await AddRepositoryAsync(ctx, "second-repo");
        var active = 0;
        var maxActive = new int[1];
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.WorkerBridge.RespondAsync("SyncRepository", async (_, _) =>
        {
            var now = Interlocked.Increment(ref active);
            Observe(maxActive, now);
            firstEntered.TrySetResult();
            try
            {
                await release.Task;
                return new WorkerCommandResponse(true, SyncResponse(), null);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var run = git.SyncAsync(ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync());
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref maxActive[0]));
        release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref maxActive[0]));
        Assert.Equal(2, ctx.WorkerBridge.Calls.Count(call => call.Command == "SyncRepository"));
    }

    private static void Observe(int[] maxActive, int now)
    {
        int snapshot;
        do
        {
            snapshot = Volatile.Read(ref maxActive[0]);
            if (now <= snapshot)
                return;
        }
        while (Interlocked.CompareExchange(ref maxActive[0], now, snapshot) != snapshot);
    }

    private static async Task AddRepositoryAsync(SyncStateTestContext ctx, string name)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GrayMoon.App.Data.AppDbContext>();
        var connectorId = await db.Connectors.Select(connector => connector.ConnectorId).FirstAsync();
        var repo = new Repository
        {
            ConnectorId = connectorId,
            RepositoryName = name,
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = $"https://github.com/acme/{name}.git",
        };
        db.Repositories.Add(repo);
        await db.SaveChangesAsync();
        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = repo.RepositoryId,
            BranchName = "main",
            DefaultBranchName = "main",
            SyncStatus = RepoSyncStatus.InSync,
        });
        await db.SaveChangesAsync();
    }

    private static object SyncResponse() => new
    {
        success = true,
        version = "2.0.0",
        branch = "main",
        defaultBranch = "main",
        outgoingCommits = 0,
        incomingCommits = 0,
        defaultBranchBehind = 0,
        defaultBranchAhead = 0,
        hasUpstream = true,
        upstreamProbed = true,
        localBranches = new[] { "main" },
        remoteBranches = new[] { "origin/main" },
        tags = Array.Empty<string>(),
        projects = Array.Empty<object>(),
    };
}
