using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Components.Features;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.GitHub;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Feature removal must leave the normal Workspace checkout untouched: no return-to-default,
/// no checkout of ParentBranchName, and no Workspace status refresh. That refresh is a separate
/// operation and does not run as part of background removal.
/// </summary>
public sealed class RemoveFeatureWorkspaceRefreshTests
{
    /// <summary>
    /// Canned InspectWorktree response for a clean, present, pushed, up-to-date worktree (used so tests
    /// never depend on real directories on the App host). Default ahead counts are 0 so existing tests
    /// keep their original "nothing pending" meaning now that OutgoingCommits/AheadOfDefault come live
    /// from this response instead of a cached database value.
    /// </summary>
    /// <summary>
    /// "feat-refresh" matches every Feature name seeded in this file; the worktree is never drifted
    /// here (09 SB-2 drift is covered separately), so the Feature-branch fields default to mirror the
    /// checked-out branch's own ahead/upstream counts, exactly what a real, non-drifted InspectWorktree
    /// response would report.
    /// </summary>
    private static object CleanInspectWorktree(
        bool exists = true,
        bool? isDirty = false,
        bool? hasUpstream = true,
        int? aheadOfUpstream = 0,
        int? aheadOfDefault = 0,
        string? branch = "feat-refresh",
        int? stagedCount = 0,
        int? unstagedCount = 0,
        int? untrackedCount = 0,
        int? conflictCount = 0,
        bool? hasOperationInProgress = false) => new
    {
        exists,
        isDirty,
        stagedCount,
        unstagedCount,
        untrackedCount,
        conflictCount,
        hasOperationInProgress,
        hasUpstream,
        aheadOfUpstream,
        aheadOfDefault,
        branch,
        featureBranchExists = true,
        featureBranchAheadOfDefault = aheadOfDefault,
        featureBranchHasUpstream = hasUpstream,
        featureBranchAheadOfUpstream = aheadOfUpstream,
    };

    private static object SyncResponse(string branch = "main", int incoming = 4) => new
    {
        success = true,
        version = "2.0.0",
        branch,
        defaultBranch = "main",
        outgoingCommits = 0,
        incomingCommits = incoming,
        defaultBranchBehind = 0,
        defaultBranchAhead = 0,
        hasUpstream = true,
        upstreamProbed = true,
        localBranches = new[] { branch, "main" },
        remoteBranches = new[] { "origin/main" },
        tags = Array.Empty<string>(),
        projects = Array.Empty<object>(),
    };

    private static void AssertNoWorkspaceMutationCommands(FakeWorkerBridge bridge)
    {
        Assert.DoesNotContain(bridge.Calls, c => c.Command == "ReturnToDefaultBranch");
        Assert.DoesNotContain(bridge.Calls, c =>
            c.Command.Contains("Checkout", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(bridge.Calls, c =>
            c.Command.Contains("Pull", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Remove_keeps_Workspace_on_non_default_branch_and_does_not_refresh_it()
    {
        // A: Workspace on topic-A, default main - removal must not return to default or refresh.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx, parentBranchName: "topic-A");

        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "topic-A";
            link.DefaultBranchName = "main";
            link.OutgoingCommits = 0;
            link.IncomingCommits = 0;
            link.BranchHasUpstream = false;
        });

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse(branch: "topic-A", incoming: 3));
        ctx.WorkerBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions
            {
                AllowDiscardUncommitted = true,
                AllowForceDeleteLocalBranches = true,
            });

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "SyncRepository");
        AssertNoWorkspaceMutationCommands(ctx.WorkerBridge);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("topic-A", link.BranchName);
        Assert.Equal(0, link.IncomingCommits);
        Assert.False(link.BranchHasUpstream);

        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Workspace_remote_refresh_fetches_and_stores_what_the_fetch_changes_without_a_Sync()
    {
        // Merged upstream commits: the Workspace must show it is behind once the step has run, from a fetch alone.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "topic-A";
            link.DefaultBranchName = "main";
            link.GitVersion = "1.0.0";
            link.OutgoingCommits = 0;
            link.IncomingCommits = 0;
            link.DefaultBranchBehindCommits = 0;
            link.DefaultBranchAheadCommits = 0;
            link.BranchHasUpstream = false;
        });
        await SeedBranchRowsAsync(ctx, ("origin/merged-and-deleted", true), ("origin/main", true), ("topic-A", false));

        ctx.WorkerBridge.Respond("FetchCommits", new
        {
            success = true,
            outgoingCommits = 0,
            incomingCommits = 3,
            hasUpstream = true,
            defaultBranchBehind = 7,
            defaultBranchAhead = 1,
            commitCountsProbed = true,
            tags = new[] { "v2.0.0", "v1.0.0" },
            localBranches = new[] { "topic-A", "main" },
            remoteBranches = new[] { "main", "topic-A" },
            defaultBranch = "main",
            branchesProbed = true,
        });

        var special = await ctx.GetSpecialContextIdAsync();
        var statusBefore = (await ctx.ReadLinkAsync()).SyncStatus;
        await using var scope = ctx.CreateScope();
        var refresh = scope.ServiceProvider.GetRequiredService<IWorkspaceRemoteRefresh>();
        await refresh.RunAsync(ctx.WorkspaceId, special);

        // One fetch per repository and nothing else: no Sync, no GitVersion, no project or file-version work.
        var counts = WorkerCommandCounts.Of(ctx.WorkerBridge);
        counts.AssertCount("FetchCommits", 1);
        Assert.Equal(["FetchCommits"], counts.Commands);
        AssertNoWorkspaceMutationCommands(ctx.WorkerBridge);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal(3, link.IncomingCommits);
        Assert.Equal(7, link.DefaultBranchBehindCommits);
        Assert.Equal(1, link.DefaultBranchAheadCommits);
        Assert.True(link.BranchHasUpstream);
        // What is checked out and what was derived from it are not touched by a fetch.
        Assert.Equal("topic-A", link.BranchName);
        Assert.Equal("1.0.0", link.GitVersion);
        Assert.Equal(statusBefore, link.SyncStatus);

        var branches = await ctx.ReadBranchesAsync();
        Assert.DoesNotContain(branches, b => b.BranchName == "origin/merged-and-deleted");
        Assert.Contains(branches, b => b is { BranchName: "origin/topic-A", IsRemote: true });
        Assert.Contains(branches, b => b is { BranchName: "v2.0.0", IsTag: true });
        Assert.Contains(ctx.Broadcasts, b => b.Method == "WorkspaceSynced");
    }

    [Fact]
    public async Task An_explicit_Sync_after_the_remote_refresh_changes_no_remote_observation()
    {
        // The explicit Sync is the oracle: what the fetch-only step stored about the remote is what a Sync stores.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "topic-A";
            link.DefaultBranchName = "main";
            link.IncomingCommits = 0;
            link.BranchHasUpstream = false;
        });
        await SeedBranchRowsAsync(ctx, ("origin/merged-and-deleted", true));

        string[] localBranches = ["topic-A", "main"];
        string[] remoteBranches = ["main", "topic-A"];
        string[] tags = ["v2.0.0", "v1.0.0"];
        ctx.WorkerBridge.Respond("FetchCommits", new
        {
            success = true,
            outgoingCommits = 2,
            incomingCommits = 3,
            hasUpstream = true,
            defaultBranchBehind = 7,
            defaultBranchAhead = 1,
            commitCountsProbed = true,
            tags,
            localBranches,
            remoteBranches,
            defaultBranch = "main",
            branchesProbed = true,
        });
        ctx.WorkerBridge.Respond("SyncRepository", new
        {
            success = true,
            version = "2.0.0",
            branch = "topic-A",
            commit = "1111111111111111111111111111111111111111",
            defaultBranch = "main",
            outgoingCommits = 2,
            incomingCommits = 3,
            defaultBranchBehind = 7,
            defaultBranchAhead = 1,
            hasUpstream = true,
            upstreamProbed = true,
            localBranches,
            remoteBranches,
            tags,
            projects = Array.Empty<object>(),
        });
        ctx.WorkerBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

        var special = await ctx.GetSpecialContextIdAsync();
        await using (var scope = ctx.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWorkspaceRemoteRefresh>().RunAsync(ctx.WorkspaceId, special);
        var afterFetch = await ReadRemoteObservationAsync(ctx);

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<GrayMoon.App.Services.Git.WorkspaceGitService>()
                .SyncAsync(ctx.WorkspaceId, special);
        }

        var afterSync = await ReadRemoteObservationAsync(ctx);
        Assert.Equal(afterFetch, afterSync);
        Assert.Equal("out=2 in=3 behind=7 ahead=1 upstream=True newerTag= | origin/main origin/topic-A | main* topic-A | v2.0.0 v1.0.0", afterSync);
    }

    [Fact]
    public async Task A_repository_whose_fetch_fails_keeps_its_stored_counts_and_does_not_fail_the_refresh()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.IncomingCommits = 4;
            link.BranchHasUpstream = true;
        });
        await SeedBranchRowsAsync(ctx, ("origin/keep-me", true));
        ctx.WorkerBridge.Respond("FetchCommits", new { success = false, errorMessage = "Git fetch failed." });

        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkspaceRemoteRefresh>().RunAsync(ctx.WorkspaceId, special);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal(4, link.IncomingCommits);
        Assert.True(link.BranchHasUpstream);
        Assert.Contains(await ctx.ReadBranchesAsync(), b => b.BranchName == "origin/keep-me");
    }

    [Fact]
    public async Task Removals_that_arrive_together_share_one_remote_refresh()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letFetchFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.WorkerBridge.RespondAsync("FetchCommits", async (_, ct) =>
        {
            fetchStarted.TrySetResult();
            await letFetchFinish.Task.WaitAsync(ct);
            return new WorkerCommandResponse(true, new { success = true, commitCountsProbed = false }, null);
        });

        var special = await ctx.GetSpecialContextIdAsync();
        var refresh = ctx.Resolve<IWorkspaceRemoteRefresh>();
        var first = refresh.RunAsync(ctx.WorkspaceId, special);
        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = refresh.RunAsync(ctx.WorkspaceId, special);
        letFetchFinish.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        WorkerCommandCounts.Of(ctx.WorkerBridge).AssertCount("FetchCommits", 1);
    }

    [Fact]
    public async Task The_removal_queue_refreshes_the_Workspace_with_a_fetch_and_sends_no_Sync()
    {
        // Departure, queue and the removal itself: zero SyncRepository, and no GitVersion, project or file-version
        // command for the Workspace. The one step before the removal is the fetch.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
        ctx.WorkerBridge.Respond("FetchCommits", new
        {
            success = true,
            outgoingCommits = 0,
            incomingCommits = 6,
            hasUpstream = true,
            commitCountsProbed = true,
        });

        using var queue = new FeatureRemovalQueue(
            ctx.Resolve<IServiceScopeFactory>(),
            new NoFolderCleanup(),
            ctx.Resolve<IWorkspaceRemoteRefresh>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FeatureRemovalQueue>.Instance);
        var completed = new TaskCompletionSource<FeatureRemovalCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Completed += c => completed.TrySetResult(c);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await queue.TryEnqueueAsync(
                featureContextId,
                new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true }));
            var outcome = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(outcome.Result.Success, outcome.Result.Error);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        var counts = WorkerCommandCounts.Of(ctx.WorkerBridge);
        counts.AssertNone(
            "SyncRepository", "RefreshRepositoryVersion", "GetRepositoryVersion", "GetGitVersionAtDefaultTip",
            "CheckFileVersions", "SyncRepositoryDependencies", "RefreshRepositoryProjects");
        counts.AssertCount("FetchCommits", 1);
        // The fetch ran before anything of the Feature was removed.
        var commands = ctx.WorkerBridge.Calls.Select(c => c.Command).ToList();
        Assert.True(
            commands.IndexOf("FetchCommits") < commands.IndexOf(WorkerHubMethods.RemoveGitWorktree),
            $"Expected the fetch before the removal: {string.Join(", ", commands)}");
        Assert.Equal(6, (await ctx.ReadLinkAsync()).IncomingCommits);
    }

    private static async Task SeedBranchRowsAsync(SyncStateTestContext ctx, params (string Name, bool IsRemote)[] branches)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (name, isRemote) in branches)
        {
            db.RepositoryBranches.Add(new RepositoryBranch
            {
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                BranchName = name,
                IsRemote = isRemote,
                LastSeenAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Everything a fetch can change, as one comparable line: counts, upstream, newer tag, then the remote, local and tag rows.</summary>
    private static async Task<string> ReadRemoteObservationAsync(SyncStateTestContext ctx)
    {
        var link = await ctx.ReadLinkAsync();
        var rows = await ctx.ReadBranchesAsync();
        string Rows(Func<RepositoryBranch, bool> filter, Func<RepositoryBranch, object> order) => string.Join(
            " ", rows.Where(filter).OrderBy(order).Select(b => b.BranchName + (b.IsDefault ? "*" : "")));
        return $"out={link.OutgoingCommits} in={link.IncomingCommits} behind={link.DefaultBranchBehindCommits} ahead={link.DefaultBranchAheadCommits} "
            + $"upstream={link.BranchHasUpstream} newerTag={link.HasNewerTag} | "
            + $"{Rows(b => b.IsRemote && !b.IsTag, b => b.BranchName)} | {Rows(b => !b.IsRemote && !b.IsTag, b => b.BranchName)} | {Rows(b => b.IsTag, b => b.SortIndex)}";
    }

    private sealed class NoFolderCleanup : IFeatureFolderCleanupService
    {
        public void RequestSweep(int workspaceId)
        {
        }

        public Task SweepAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void ArmRetry(int workspaceId)
        {
        }
    }

    [Fact]
    public async Task Remove_does_not_checkout_ParentBranchName_or_default_when_Workspace_differs()
    {
        // B: ParentBranchName=develop, current Workspace=hotfix/foo - neither develop nor main checked out.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx, parentBranchName: "develop");

        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "hotfix/foo";
            link.DefaultBranchName = "main";
            link.OutgoingCommits = 1;
            link.IncomingCommits = 0;
            link.BranchHasUpstream = true;
        });

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse(branch: "hotfix/foo", incoming: 2));
        ctx.WorkerBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions
            {
                AllowDiscardUncommitted = true,
                AllowForceDeleteLocalBranches = true,
            });

        Assert.True(result.Success, result.Error);
        AssertNoWorkspaceMutationCommands(ctx.WorkerBridge);
        Assert.DoesNotContain(
            ctx.WorkerBridge.Calls,
            c => c.Args?.ToString()?.Contains("develop", StringComparison.Ordinal) == true
                 && c.Command != "SyncRepository");

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("hotfix/foo", link.BranchName);
        Assert.NotEqual("develop", link.BranchName);
        Assert.NotEqual("main", link.BranchName);
    }

    [Fact]
    public async Task Remove_succeeds_when_ParentBranchName_no_longer_exists()
    {
        // C: ParentBranchName=old-topic (deleted); Workspace on main - no recreate/checkout of old-topic.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx, parentBranchName: "old-topic");

        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "main";
            link.DefaultBranchName = "main";
            link.OutgoingCommits = 0;
            link.IncomingCommits = 1;
            link.BranchHasUpstream = true;
        });

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse(branch: "main", incoming: 1));
        ctx.WorkerBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions
            {
                AllowDiscardUncommitted = true,
                AllowForceDeleteLocalBranches = true,
            });

        Assert.True(result.Success, result.Error);
        AssertNoWorkspaceMutationCommands(ctx.WorkerBridge);
        Assert.DoesNotContain(
            ctx.WorkerBridge.Calls,
            c => c.Args?.ToString()?.Contains("old-topic", StringComparison.Ordinal) == true);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("main", link.BranchName);
        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Remove_when_Workspace_already_on_default_does_not_refresh_or_return_to_default()
    {
        // D: Already on default - removal neither refreshes nor takes a special default-branch path.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx, parentBranchName: "main");

        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "main";
            link.DefaultBranchName = "main";
            link.OutgoingCommits = 0;
            link.IncomingCommits = 0;
            link.BranchHasUpstream = false;
        });

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse(branch: "main", incoming: 5));
        ctx.WorkerBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions
            {
                AllowDiscardUncommitted = true,
                AllowForceDeleteLocalBranches = true,
            });

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "SyncRepository");
        AssertNoWorkspaceMutationCommands(ctx.WorkerBridge);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("main", link.BranchName);
        Assert.Equal(0, link.IncomingCommits);
        Assert.False(link.BranchHasUpstream);

        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Remove_when_worktree_delete_fails_does_not_report_success_or_refresh_workspace()
    {
        // E: Worktree deletion failure -> NeedsRepair; no branch delete; no post-success refresh.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(
            WorkerHubMethods.RemoveGitWorktree,
            data: null,
            success: false,
            error: "error: failed to delete 'features/feat-refresh': Permission denied");

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions
            {
                AllowDiscardUncommitted = true,
                AllowForceDeleteLocalBranches = true,
            });

        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error);
        AssertNoWorkspaceMutationCommands(ctx.WorkerBridge);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "SyncRepository");
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "DeleteBranch");

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.WorkspaceFeatureContexts.AnyAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value));
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.Name == "feat-refresh" && f.WorkspaceId == ctx.WorkspaceId);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);
    }

    [Fact]
    public async Task Remove_cleans_all_repositories_and_issues_per_repo_worker_commands()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureWithTwoReposAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse(branch: "main", incoming: 1));
        ctx.WorkerBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions
            {
                AllowDiscardUncommitted = true,
                AllowForceDeleteLocalBranches = true,
            });

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, ctx.WorkerBridge.Calls.Count(c => c.Command == WorkerHubMethods.RemoveGitWorktree));
        Assert.Equal(2, ctx.WorkerBridge.Calls.Count(c => c.Command == "DeleteBranch"));
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "SyncRepository");
        AssertNoWorkspaceMutationCommands(ctx.WorkerBridge);
        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Remove_deletes_the_Feature_branches_with_hooks_off()
    {
        // The deletes run in the main checkouts. With hooks on, each one queued a sync notification carrying the main
        // checkout's state, and that backlog later overwrote the result of a pull the user ran in the Workspace.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureWithTwoReposAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions
            {
                AllowDiscardUncommitted = true,
                AllowForceDeleteLocalBranches = true,
            });

        Assert.True(result.Success, result.Error);
        var deletes = ctx.WorkerBridge.Calls.Where(c => c.Command == "DeleteBranch").ToList();
        Assert.NotEmpty(deletes);
        Assert.All(deletes, delete =>
            Assert.True(JsonSerializer.SerializeToElement(delete.Args).GetProperty("skipHooks").GetBoolean()));
    }

    [Fact]
    public async Task Remove_partial_worktree_failure_still_cleans_successful_repos_and_keeps_Feature()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureWithTwoReposAsync(ctx);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        var removeAttempts = 0;
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, _ =>
        {
            var attempt = Interlocked.Increment(ref removeAttempts);
            if (attempt == 1)
                return new WorkerCommandResponse(false, null, "Permission denied on first repo");
            return new WorkerCommandResponse(true, new { success = true }, null);
        });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions
            {
                AllowDiscardUncommitted = true,
                AllowForceDeleteLocalBranches = true,
            });

        Assert.False(result.Success);
        Assert.Equal(2, ctx.WorkerBridge.Calls.Count(c => c.Command == WorkerHubMethods.RemoveGitWorktree));
        // Successful repo still deletes its Feature branch; failed repo skips DeleteBranch.
        Assert.Equal(1, ctx.WorkerBridge.Calls.Count(c => c.Command == "DeleteBranch"));
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "SyncRepository");

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.WorkspaceFeatureContexts.AnyAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value));
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.Name == "feat-refresh" && f.WorkspaceId == ctx.WorkspaceId);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);
        // D2: a failed repo row stays Removing with its error in LastError, not row-level NeedsRepair.
        var failedRow = await db.WorkspaceFeatureRepositories.SingleAsync(r =>
            r.WorkspaceFeatureContextId == featureContextId.Value
            && r.State == WorkspaceFeatureRepositoryState.Removing);
        Assert.Contains("Permission denied", failedRow.LastError);
    }

    [Fact]
    public async Task Analyze_dirty_Feature_worktree_is_not_automatically_safe()
    {
        // F
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var plan = await AssertAnalyzeNotAutomaticallySafeAsync(ctx, CleanInspectWorktree(isDirty: true, unstagedCount: 1));
        Assert.True(plan.HasUncommittedChanges);
        Assert.False(plan.HasStagedChanges);
        Assert.False(plan.HasConflicts);
    }

    [Fact]
    public async Task Analyze_staged_Feature_worktree_is_not_automatically_safe()
    {
        // G
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var plan = await AssertAnalyzeNotAutomaticallySafeAsync(ctx, CleanInspectWorktree(isDirty: true, stagedCount: 1));
        Assert.False(plan.HasUncommittedChanges);
        Assert.True(plan.HasStagedChanges);
        Assert.False(plan.HasConflicts);
    }

    [Fact]
    public async Task Analyze_conflicted_Feature_worktree_is_not_automatically_safe()
    {
        // H
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var plan = await AssertAnalyzeNotAutomaticallySafeAsync(ctx, CleanInspectWorktree(isDirty: true, conflictCount: 1));
        Assert.True(plan.HasConflicts);
    }

    [Fact]
    public async Task Analyze_unfinished_rebase_in_a_clean_worktree_is_not_automatically_safe()
    {
        // A rebase can be stopped with nothing changed in the working tree; removing the worktree would drop it.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var plan = await AssertAnalyzeNotAutomaticallySafeAsync(ctx, CleanInspectWorktree(hasOperationInProgress: true));
        Assert.True(plan.HasConflicts);
        Assert.False(plan.HasUncommittedChanges);
    }

    [Fact]
    public async Task Analyze_reads_each_worktree_with_one_Worker_command()
    {
        // The analysis asked for GetGitChangeStatus besides InspectWorktree: git status twice per repository, on a
        // machine where a git process costs over a second (logs of 2026-11-25).
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.IsAutomaticallySafe);
        Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.InspectWorktree);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "GetGitChangeStatus");
    }

    [Fact]
    public async Task Analyze_status_failure_is_not_automatically_safe()
    {
        // I
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree(isDirty: null));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(RemoveFeatureClassification.Completed, plan.Classification);
        Assert.False(plan.IsAutomaticallySafe);
        var repo = Assert.Single(plan.Repositories);
        Assert.False(repo.LiveStatusEstablished);
    }

    [Fact]
    public async Task Remove_with_empty_options_succeeds_when_not_auto_safe_only_because_live_status_failed()
    {
        // The inspection did not say whether the worktree is dirty: not automatically safe, but the dirty flags stay
        // false so the dialog shows no discard checkbox and enables Remove. The server must not demand authorization.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree(isDirty: null));
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse());
        ctx.WorkerBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);
        Assert.False(plan.IsAutomaticallySafe);
        Assert.False(Assert.Single(plan.Repositories).LiveStatusEstablished);

        var result = await ops.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions());
        Assert.True(result.Success, result.Error);
        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Analyze_clean_completed_Feature_is_automatically_safe()
    {
        // J
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(RemoveFeatureClassification.Completed, plan.Classification);
        Assert.True(plan.IsAutomaticallySafe);
        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.LiveStatusEstablished);
        Assert.False(repo.HasUncommittedChanges);
        Assert.False(repo.HasStagedChanges);
        Assert.False(repo.HasConflicts);
    }

    [Fact]
    public async Task Analyze_Feature_worktree_is_not_automatically_safe_when_Worker_cannot_confirm_disk_state()
    {
        // Docker / disconnected-Worker: InspectWorktree cannot be reached, so the repository is
        // Unknown, not Missing - it must not be treated as automatically safe to remove.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.WorkerBridge.IsWorkerConnected = false;

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        Assert.False(plan.IsAutomaticallySafe);
        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.WorktreeStatusUnknown);
        Assert.False(repo.WorktreeExists);
        Assert.Contains("Could not check this repository", repo.Warning);

        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        // Without a Worker nothing is attempted: the repository is kept and the result says why.
        Assert.False(result.Success);
        Assert.Contains("Worker not connected", result.Error);
        var blocked = Assert.Single(result.Outcome!.Targets).Step(RemoveFeatureSteps.Worktree)!;
        Assert.Equal(OperationOutcomeStatus.Blocked, blocked.Status);
        Assert.Equal(OperationReasonCodes.WorkerUnavailable, blocked.ReasonCode);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.RemoveGitWorktree);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "DeleteBranch");
    }

    [Fact]
    public async Task Analyze_Feature_worktree_missing_per_Worker_is_shown_as_missing_not_unknown()
    {
        // Fake Worker says missing: existing behaviour (Missing, not Unknown) is kept.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree(exists: false, isDirty: null));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        var repo = Assert.Single(plan.Repositories);
        Assert.False(repo.WorktreeExists);
        Assert.False(repo.WorktreeStatusUnknown);
        Assert.Equal(RemoveFeatureClassification.NeedsRepair, plan.Classification);
    }

    [Fact]
    public async Task Analyze_Feature_worktree_reports_Worker_exists_and_dirty_even_when_path_is_not_on_the_App_host()
    {
        // Docker topology: the App never reads this path from local disk, so a path under a
        // nonexistent drive on the test machine must still be reported exactly as the Worker says.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"Z:\nope\does-not-exist\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree(exists: true, isDirty: true, unstagedCount: 1));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        Assert.False(plan.IsAutomaticallySafe);
        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.WorktreeExists);
        Assert.False(repo.WorktreeStatusUnknown);
        Assert.True(repo.HasUncommittedChanges);
    }

    [Fact]
    public async Task Analyze_after_permission_denied_keeps_merged_feature_automatically_safe_when_live_clean()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedRemovableFeatureAsync(ctx);
        const string locked = "error: failed to delete 'features/mime-magic-only/EDX1': Permission denied";

        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.WorkspaceFeatureRepositories.SingleAsync(r => r.WorkspaceFeatureContextId == featureContextId.Value);
            row.WorktreePath = worktreePath;
            row.State = WorkspaceFeatureRepositoryState.NeedsRepair;
            row.LastError = locked;
            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = featureContextId.Value,
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                BranchName = "feat-refresh",
                OutgoingCommits = 0,
                BranchHasUpstream = true,
            });
            db.WorkspaceRepositoryContextPullRequests.Add(new WorkspaceRepositoryContextPullRequest
            {
                WorkspaceFeatureContextId = featureContextId.Value,
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                PullRequestNumber = 55,
                State = "closed",
                MergedAt = DateTimeOffset.UtcNow,
                LastCheckedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(
            WorkerHubMethods.RemoveGitWorktree,
            data: null,
            success: false,
            error: locked);

        await using var read = ctx.CreateScope();
        var ops = read.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(RemoveFeatureClassification.Completed, plan.Classification);
        Assert.True(plan.IsAutomaticallySafe);
        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.WorktreeExists);
        Assert.Contains("Permission denied", repo.Warning);

        var retry = await ops.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions());
        Assert.False(retry.Success);
        Assert.DoesNotContain("not automatically safe", retry.Error);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "SyncRepository");
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "DeleteBranch");
    }

    [Fact]
    public async Task Analyze_fresh_Feature_with_no_PR_is_completed_and_automatically_safe_when_clean()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedRemovableFeatureAsync(ctx);
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.WorkspaceFeatureRepositories.SingleAsync(r => r.WorkspaceFeatureContextId == featureContextId.Value);
            row.WorktreePath = worktreePath;
            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = featureContextId.Value,
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                BranchName = "feat-refresh",
                OutgoingCommits = 0,
                BranchHasUpstream = true,
            });
            // No WorkspaceRepositoryContextPullRequest row - never-created PR.
            await db.SaveChangesAsync();
        }

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());

        await using var read = ctx.CreateScope();
        var ops = read.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(RemoveFeatureClassification.Completed, plan.Classification);
        Assert.True(plan.IsAutomaticallySafe);
        var repo = Assert.Single(plan.Repositories);
        Assert.Null(repo.PullRequestNumber);
        Assert.True(string.IsNullOrWhiteSpace(repo.PullRequestState));
    }

    [Fact]
    public async Task Analyze_never_pushed_clean_Feature_is_automatically_safe()
    {
        // Never pushed: the Worker reports no upstream and a null ahead-of-upstream count.
        // That null means "no remote", not an unknown count, so a clean finished Feature is safe
        // and the Remove button is green.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedRemovableFeatureAsync(ctx);
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.WorkspaceFeatureRepositories.SingleAsync(r => r.WorkspaceFeatureContextId == featureContextId.Value);
            row.WorktreePath = worktreePath;
            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = featureContextId.Value,
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                BranchName = "feat-refresh",
                OutgoingCommits = 0,
                BranchHasUpstream = false,
            });
            await db.SaveChangesAsync();
        }

        ctx.WorkerBridge.Respond(
            WorkerHubMethods.InspectWorktree,
            CleanInspectWorktree(hasUpstream: false, aheadOfUpstream: null, aheadOfDefault: 0));

        await using var read = ctx.CreateScope();
        var ops = read.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(RemoveFeatureClassification.Completed, plan.Classification);
        Assert.True(plan.IsAutomaticallySafe);
        Assert.False(RemoveFeatureModal.RemoveCtaIsDestructive(plan));
        var repo = Assert.Single(plan.Repositories);
        Assert.False(repo.FeatureBranchHasUpstream);
        Assert.Null(repo.EffectiveOutgoingCommits);
        Assert.Equal(0, repo.EffectiveAheadOfDefault);
    }

    [Fact]
    public async Task Analyze_live_outgoing_commits_without_pull_request_is_not_completed_or_safe()
    {
        // F-9/U-18: live commits ahead of the default branch with no pull request yet must not be
        // treated as "Completed, never opened" - the branch is pushed and awaiting review.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedFeatureWithWorktreeNoPrAsync(ctx, worktreePath);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree(aheadOfUpstream: 2, aheadOfDefault: 2));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(RemoveFeatureClassification.Active, plan.Classification);
        Assert.False(plan.IsAutomaticallySafe);
        var repo = Assert.Single(plan.Repositories);
        Assert.Equal(2, repo.AheadOfDefault);
        Assert.Null(repo.PullRequestNumber);
    }

    [Fact]
    public async Task Analyze_unknown_live_ahead_count_is_not_automatically_safe()
    {
        // An older Worker (or a repo InspectWorktree could not compute an ahead-of-upstream count
        // for) reports null, not zero - null must never be treated as "nothing pending".
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree(aheadOfUpstream: null));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(RemoveFeatureClassification.Completed, plan.Classification);
        Assert.False(plan.IsAutomaticallySafe);
        var repo = Assert.Single(plan.Repositories);
        Assert.Null(repo.OutgoingCommits);
    }

    [Fact]
    public async Task Analyze_old_Worker_InspectWorktree_shape_without_ahead_fields_still_deserializes()
    {
        // Worker-compatibility: a Worker older than this unit's App only sends exists/isDirty for
        // InspectWorktree. The App must still deserialize that response (ignoring the fields it does
        // not have) and treat the missing ahead counts as unknown, not zero.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, new { exists = true, isDirty = false });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        var repo = Assert.Single(plan.Repositories);
        Assert.False(repo.WorktreeStatusUnknown);
        Assert.True(repo.WorktreeExists);
        Assert.Null(repo.OutgoingCommits);
        Assert.Null(repo.AheadOfDefault);
        Assert.False(repo.HasUpstream);
        Assert.False(plan.IsAutomaticallySafe);
    }

    [Fact]
    public async Task Analyze_pull_request_refresh_failure_is_not_automatically_safe()
    {
        // Offline / rate-limited GitHub must make PR state Unknown, not "no pull request".
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);

        await using (var pauseScope = ctx.CreateScope())
        {
            var rateLimitTracker = pauseScope.ServiceProvider.GetRequiredService<IGitHubRateLimitTracker>();
            rateLimitTracker.PauseUntil("github-prod", DateTimeOffset.UtcNow.AddMinutes(5));
        }

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        Assert.True(plan.PullRequestStatusUnknown);
        Assert.False(plan.IsAutomaticallySafe);
    }

    [Fact]
    public async Task Remove_with_empty_options_succeeds_when_not_auto_safe_only_because_PR_status_is_unknown()
    {
        // D3 gate fix: PullRequestStatusUnknown makes IsAutomaticallySafe false, but no discard/force/
        // unlock checkbox applies. The dialog enables Remove; the server must not demand authorization.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);

        await using (var pauseScope = ctx.CreateScope())
        {
            var rateLimitTracker = pauseScope.ServiceProvider.GetRequiredService<IGitHubRateLimitTracker>();
            rateLimitTracker.PauseUntil("github-prod", DateTimeOffset.UtcNow.AddMinutes(5));
        }

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse());
        ctx.WorkerBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);
        Assert.True(plan.PullRequestStatusUnknown);
        Assert.False(plan.IsAutomaticallySafe);

        var result = await ops.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions());
        Assert.True(result.Success, result.Error);
        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Remove_with_empty_options_succeeds_when_not_auto_safe_only_because_outgoing_is_unknown()
    {
        // Null EffectiveOutgoingCommits (older Worker / unknown ahead-of-upstream) is not automatically
        // safe, but no discard/force checkbox applies when AheadOfDefault is also unknown/zero.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree(aheadOfUpstream: null));
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse());
        ctx.WorkerBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);
        Assert.False(plan.IsAutomaticallySafe);
        Assert.Null(Assert.Single(plan.Repositories).OutgoingCommits);

        var result = await ops.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions());
        Assert.True(result.Success, result.Error);
        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Remove_with_empty_options_succeeds_for_abandoned_Feature_that_is_clean()
    {
        // Abandoned (closed without merge) is never automatically safe. When the worktree is clean and
        // AheadOfDefault is 0, no checkbox is shown; Remove must still succeed without authorization.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        // Token-less GitHub in this harness would refresh the closed-unmerged row away to "no PR"
        // (Completed). Pause it so Abandoned is what the server actually sees.
        await using (var pauseScope = ctx.CreateScope())
        {
            var rateLimitTracker = pauseScope.ServiceProvider.GetRequiredService<IGitHubRateLimitTracker>();
            rateLimitTracker.PauseUntil("github-prod", DateTimeOffset.UtcNow.AddMinutes(5));
        }

        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.WorkspaceFeatureRepositories.SingleAsync(r => r.WorkspaceFeatureContextId == featureContextId.Value);
            row.WorktreePath = worktreePath;
            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = featureContextId.Value,
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                BranchName = "feat-refresh",
                OutgoingCommits = 0,
                BranchHasUpstream = true,
            });
            db.WorkspaceRepositoryContextPullRequests.Add(new WorkspaceRepositoryContextPullRequest
            {
                WorkspaceFeatureContextId = featureContextId.Value,
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                PullRequestNumber = 7,
                State = "closed",
                MergedAt = null,
                LastCheckedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse());
        ctx.WorkerBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

        await using var read = ctx.CreateScope();
        var ops = read.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);
        Assert.Equal(RemoveFeatureClassification.Abandoned, plan.Classification);
        Assert.False(plan.IsAutomaticallySafe);

        var result = await ops.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions());
        Assert.True(result.Success, result.Error);
        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Remove_without_discard_authorization_keeps_a_dirty_worktree_and_never_forces()
    {
        // The removal does not inspect first. It asks for a removal without force; the Worker keeps the folder and
        // says why from the worktree's own status, and the result carries that reason and the consent it would need.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, _ => new WorkerCommandResponse(
            false,
            new { success = false, errorCode = "UncommittedChanges", errorMessage = "fatal: contains modified or untracked files", failureKind = "UncommittedChanges" },
            "fatal: contains modified or untracked files"));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions());

        Assert.False(result.Success);
        Assert.Contains("uncommitted changes", result.Error, StringComparison.OrdinalIgnoreCase);
        var counts = WorkerCommandCounts.Of(ctx.WorkerBridge);
        counts.AssertNone(WorkerHubMethods.InspectWorktree, "DeleteBranch", WorkerHubMethods.CleanupFeatureFolder);
        var remove = Assert.Single(counts.ArgsOf(WorkerHubMethods.RemoveGitWorktree));
        Assert.False(JsonSerializer.SerializeToElement(remove).GetProperty("force").GetBoolean());
        Assert.False(JsonSerializer.SerializeToElement(remove).GetProperty("unlock").GetBoolean());

        var step = Assert.Single(result.Outcome!.Targets).Step(RemoveFeatureSteps.Worktree)!;
        Assert.Equal(OperationOutcomeStatus.Blocked, step.Status);
        Assert.Equal(OperationReasonCodes.UncommittedChanges, step.ReasonCode);
        Assert.Equal(OperationAuthorizations.DiscardUncommittedChanges, step.RequiredAuthorization);
    }

    [Fact]
    public async Task Remove_without_force_authorization_removes_the_worktree_and_keeps_an_unmerged_branch()
    {
        // Branch deletion is its own decision. The folder goes; the branch with commits that are not merged stays
        // and is reported as kept, and nothing is force-deleted.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedFeatureWithWorktreeNoPrAsync(ctx, worktreePath);
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond(
            "DeleteBranch",
            data: new { success = false, errorMessage = "error: The branch 'feat-refresh' is not fully merged.", failureKind = "NotFullyMerged", branchSha = "1111111111111111111111111111111111111111" },
            success: false,
            error: "error: The branch 'feat-refresh' is not fully merged.");

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions());

        Assert.True(result.Success, result.Error);
        var report = Assert.Single(result.RemoveFeatureReport!);
        Assert.True(report.WorktreeRemoved);
        Assert.Equal(RemoveFeatureBranchOutcome.KeptUnmerged, report.BranchOutcome);
        Assert.All(
            WorkerCommandCounts.Of(ctx.WorkerBridge).ArgsOf("DeleteBranch"),
            args => Assert.False(JsonSerializer.SerializeToElement(args).GetProperty("force").GetBoolean()));
        var local = Assert.Single(result.Outcome!.Targets).Step(RemoveFeatureSteps.LocalBranch)!;
        Assert.Equal(OperationOutcomeStatus.Blocked, local.Status);
        Assert.Equal(OperationReasonCodes.UnmergedCommits, local.ReasonCode);
        Assert.Equal(OperationAuthorizations.ForceDeleteLocalBranch, local.RequiredAuthorization);
    }

    [Fact]
    public async Task Remove_force_deletes_a_squash_merged_branch_when_the_tip_is_the_merged_commit()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AnalyzedPlan = SquashMergedPlan(ctx.WorkspaceRepositoryId, "abc123", "abc123") });

        Assert.True(result.Success, result.Error);
        var delete = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == "DeleteBranch");
        Assert.True(JsonSerializer.SerializeToElement(delete.Args).GetProperty("force").GetBoolean());
        var report = Assert.Single(result.RemoveFeatureReport!);
        Assert.Equal(RemoveFeatureBranchOutcome.Deleted, report.BranchOutcome);
    }

    [Fact]
    public async Task Remove_does_not_force_delete_when_a_merged_pull_request_does_not_cover_the_local_tip()
    {
        // The reviewed details show a merged pull request whose head is another commit than the local tip: the
        // local branch has commits the pull request never merged. Without the force consent it is deleted only if
        // git itself calls it merged, and git does not.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, new { success = true });
        ctx.WorkerBridge.Respond(
            "DeleteBranch",
            data: new { success = false, errorMessage = "error: The branch 'feat-refresh' is not fully merged.", failureKind = "NotFullyMerged", branchSha = "local-tip" },
            success: false,
            error: "error: The branch 'feat-refresh' is not fully merged.");

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            featureContextId,
            new RemoveFeatureOptions { AnalyzedPlan = SquashMergedPlan(ctx.WorkspaceRepositoryId, "local-tip", "merged-head") });

        Assert.True(result.Success, result.Error);
        var delete = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == "DeleteBranch");
        Assert.False(JsonSerializer.SerializeToElement(delete.Args).GetProperty("force").GetBoolean());
        Assert.Equal(RemoveFeatureBranchOutcome.KeptUnmerged, Assert.Single(result.RemoveFeatureReport!).BranchOutcome);
    }

    private static RemoveFeaturePlan SquashMergedPlan(int workspaceRepositoryId, string localTip, string mergedHead) => new()
    {
        Success = true,
        Classification = RemoveFeatureClassification.Completed,
        Repositories =
        [
            new RemoveFeatureRepositoryPlan
            {
                WorkspaceRepositoryId = workspaceRepositoryId,
                RepositoryName = "graymoon-api",
                WorktreeExists = true,
                LiveStatusEstablished = true,
                FeatureBranchName = "feat-refresh",
                FeatureBranchExists = true,
                FeatureBranchAheadOfDefault = 3,
                FeatureBranchSha = localTip,
                MergedPullRequestHeadSha = mergedHead,
                PullRequestMerged = true,
                PullRequestNumber = 42,
            }
        ]
    };

    // ---- 09 SB-2: analysis must judge the Feature branch, not the current checkout -----------------

    [Fact]
    public async Task Analyze_drift_with_unmerged_Feature_branch_is_not_safe_and_describes_the_kept_branch()
    {
        // The worktree has been switched to "side" in a terminal; the Feature branch itself still has
        // 2 commits not on default. The analysis must judge the Feature branch, not "side".
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedFeatureWithWorktreeNoPrAsync(ctx, worktreePath);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, new
        {
            exists = true,
            isDirty = false,
            branch = "side",
            hasUpstream = true,
            aheadOfUpstream = 0,
            aheadOfDefault = 0,
            featureBranchExists = true,
            featureBranchAheadOfDefault = 2,
            featureBranchHasUpstream = false,
            featureBranchAheadOfUpstream = (int?)null,
        });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.IsOffFeatureBranch);
        Assert.Equal("side", repo.CheckedOutBranch);
        Assert.Equal("feat-refresh", repo.FeatureBranchName);
        Assert.Equal(2, repo.EffectiveAheadOfDefault);
        Assert.Equal(RemoveFeatureClassification.Active, plan.Classification);
        Assert.False(plan.IsAutomaticallySafe);
    }

    [Fact]
    public async Task Analyze_drift_with_merged_Feature_branch_is_still_automatically_safe()
    {
        // Drifted to "side", but the Feature branch's own pull request is merged, so it is still safe.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, new
        {
            exists = true,
            isDirty = false,
            branch = "side",
            hasUpstream = true,
            aheadOfUpstream = 0,
            aheadOfDefault = 0,
            featureBranchExists = true,
            featureBranchAheadOfDefault = 0,
            featureBranchHasUpstream = true,
            featureBranchAheadOfUpstream = 0,
        });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.IsOffFeatureBranch);
        Assert.Equal(RemoveFeatureClassification.Completed, plan.Classification);
        Assert.True(plan.IsAutomaticallySafe);
    }

    [Fact]
    public async Task Analyze_old_Worker_without_Feature_branch_fields_is_not_safe_and_force_not_offered()
    {
        // An older Worker does not know about featureBranch at all: the facts are Unknown, never
        // treated as zero, and the force-delete checkbox must not be offered for this repository.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, new
        {
            exists = true,
            isDirty = false,
            hasUpstream = true,
            aheadOfUpstream = 0,
            aheadOfDefault = 0,
        });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        var repo = Assert.Single(plan.Repositories);
        Assert.Null(repo.FeatureBranchAheadOfDefault);
        Assert.Null(repo.EffectiveAheadOfDefault);
        Assert.False(plan.IsAutomaticallySafe);
        Assert.False(RemoveFeatureModal.HasUnmergedBranchAheadOfDefault(repo));
    }

    [Fact]
    public async Task Analyze_not_drifted_repository_produces_the_same_plan_as_before()
    {
        // Characterization (A4 step 0): a repository that has not drifted (checked out on its own
        // Feature branch) must classify and judge safety exactly as it did before this unit.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);

        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, CleanInspectWorktree());

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        var repo = Assert.Single(plan.Repositories);
        Assert.False(repo.IsOffFeatureBranch);
        Assert.Equal("feat-refresh", repo.CheckedOutBranch);
        Assert.Equal(RemoveFeatureClassification.Completed, plan.Classification);
        Assert.True(plan.IsAutomaticallySafe);
    }

    private static async Task<WorkspaceFeatureContextId> SeedFeatureWithWorktreeNoPrAsync(
        SyncStateTestContext ctx,
        string worktreePath)
    {
        var featureContextId = await SeedRemovableFeatureAsync(ctx);
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.WorkspaceFeatureRepositories.SingleAsync(r => r.WorkspaceFeatureContextId == featureContextId.Value);
        row.WorktreePath = worktreePath;
        db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = featureContextId.Value,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            BranchName = "feat-refresh",
            OutgoingCommits = 0,
            BranchHasUpstream = true,
        });
        await db.SaveChangesAsync();
        return featureContextId;
    }

    private static async Task<RemoveFeatureRepositoryPlan> AssertAnalyzeNotAutomaticallySafeAsync(SyncStateTestContext ctx, object inspectResponse)
    {
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, inspectResponse);

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(RemoveFeatureClassification.Completed, plan.Classification);
        Assert.False(plan.IsAutomaticallySafe);
        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.LiveStatusEstablished);
        return repo;
    }

    private static async Task<WorkspaceFeatureContextId> SeedMergedFeatureWithWorktreeAsync(
        SyncStateTestContext ctx,
        string worktreePath)
    {
        var featureContextId = await SeedRemovableFeatureAsync(ctx);
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.WorkspaceFeatureRepositories.SingleAsync(r => r.WorkspaceFeatureContextId == featureContextId.Value);
        row.WorktreePath = worktreePath;
        db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = featureContextId.Value,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            BranchName = "feat-refresh",
            OutgoingCommits = 0,
            BranchHasUpstream = true,
        });
        db.WorkspaceRepositoryContextPullRequests.Add(new WorkspaceRepositoryContextPullRequest
        {
            WorkspaceFeatureContextId = featureContextId.Value,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            PullRequestNumber = 42,
            State = "closed",
            MergedAt = DateTimeOffset.UtcNow,
            LastCheckedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return featureContextId;
    }

    private static async Task<WorkspaceFeatureContextId> SeedRemovableFeatureAsync(
        SyncStateTestContext ctx,
        string? parentBranchName = null)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feat-refresh",
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true,
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();

        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            WorktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
            ParentBranchName = parentBranchName,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }

    private static async Task<WorkspaceFeatureContextId> SeedRemovableFeatureWithTwoReposAsync(
        SyncStateTestContext ctx,
        string? parentBranchName = null)
    {
        var featureContextId = await SeedRemovableFeatureAsync(ctx, parentBranchName);

        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectorId = await db.Repositories
            .Where(r => r.RepositoryId == ctx.RepositoryId)
            .Select(r => r.ConnectorId)
            .SingleAsync();

        var secondRepo = new Repository
        {
            ConnectorId = connectorId,
            RepositoryName = "graymoon-web",
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = "https://github.com/acme/graymoon-web.git",
        };
        db.Repositories.Add(secondRepo);
        await db.SaveChangesAsync();

        var secondLink = new WorkspaceRepositoryLink
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = secondRepo.RepositoryId,
            GitVersion = "1.0.0",
            BranchName = "main",
            DefaultBranchName = "main",
            OutgoingCommits = 0,
            IncomingCommits = 0,
            BranchHasUpstream = true,
            SyncStatus = RepoSyncStatus.InSync,
            RepositoryType = ProjectType.Library,
        };
        db.WorkspaceRepositories.Add(secondLink);
        await db.SaveChangesAsync();

        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = featureContextId.Value,
            WorkspaceRepositoryId = secondLink.WorkspaceRepositoryId,
            WorktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-web",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "def456",
            ParentBranchName = parentBranchName,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return featureContextId;
    }

    private static async Task AssertFeatureGoneAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId featureContextId)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatureContexts.AnyAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value));
        Assert.False(await db.WorkspaceFeatures.AnyAsync(f => f.Name == "feat-refresh" && f.WorkspaceId == ctx.WorkspaceId));
    }
}
