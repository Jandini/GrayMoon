using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Components.Features;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.GitHub;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Feature removal must leave the normal Workspace checkout untouched: no return-to-default,
/// no checkout of ParentBranchName, and only a non-mutating status refresh of the current branch.
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
        string? branch = "feat-refresh") => new
    {
        exists,
        isDirty,
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

    private static object CleanGitChangeStatus(string branch = "feat-refresh") => new
    {
        success = true,
        snapshot = new
        {
            version = 1L,
            branchName = branch,
            headCommit = "abc123",
            changes = Array.Empty<object>(),
            scannedAt = DateTimeOffset.UtcNow,
        },
    };

    private static object DirtyGitChangeStatus(
        bool uncommitted = false,
        bool staged = false,
        bool conflicted = false,
        string branch = "feat-refresh")
    {
        var changes = new List<object>();
        if (uncommitted)
        {
            changes.Add(new
            {
                path = "dirty.txt",
                indexChange = (int)GitChangeKind.None,
                worktreeChange = (int)GitChangeKind.Modified,
                isTracked = true,
                isConflicted = false,
            });
        }

        if (staged)
        {
            changes.Add(new
            {
                path = "staged.txt",
                indexChange = (int)GitChangeKind.Modified,
                worktreeChange = (int)GitChangeKind.None,
                isTracked = true,
                isConflicted = false,
            });
        }

        if (conflicted)
        {
            changes.Add(new
            {
                path = "conflict.txt",
                indexChange = (int)GitChangeKind.Unmerged,
                worktreeChange = (int)GitChangeKind.Unmerged,
                isTracked = true,
                isConflicted = true,
            });
        }

        return new
        {
            success = true,
            snapshot = new
            {
                version = 1L,
                branchName = branch,
                headCommit = "abc123",
                isMerging = conflicted,
                changes,
                scannedAt = DateTimeOffset.UtcNow,
            },
        };
    }

    private static void AssertNoWorkspaceMutationCommands(FakeAgentBridge bridge)
    {
        Assert.DoesNotContain(bridge.Calls, c => c.Command == "ReturnToDefaultBranch");
        Assert.DoesNotContain(bridge.Calls, c =>
            c.Command.Contains("Checkout", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(bridge.Calls, c =>
            c.Command.Contains("Pull", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Remove_keeps_Workspace_on_non_default_branch_and_Syncs_current_state()
    {
        // A: Workspace on topic-A, default main - removal must not return to default.
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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse(branch: "topic-A", incoming: 3));
        ctx.AgentBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

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
        Assert.Contains(ctx.AgentBridge.Calls, c => c.Command == "SyncRepository");
        AssertNoWorkspaceMutationCommands(ctx.AgentBridge);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("topic-A", link.BranchName);
        Assert.Equal(3, link.IncomingCommits);
        Assert.True(link.BranchHasUpstream);

        await AssertFeatureGoneAsync(ctx, featureContextId);
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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse(branch: "hotfix/foo", incoming: 2));
        ctx.AgentBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

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
        AssertNoWorkspaceMutationCommands(ctx.AgentBridge);
        Assert.DoesNotContain(
            ctx.AgentBridge.Calls,
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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse(branch: "main", incoming: 1));
        ctx.AgentBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

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
        AssertNoWorkspaceMutationCommands(ctx.AgentBridge);
        Assert.DoesNotContain(
            ctx.AgentBridge.Calls,
            c => c.Args?.ToString()?.Contains("old-topic", StringComparison.Ordinal) == true);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("main", link.BranchName);
        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Remove_when_Workspace_already_on_default_still_Syncs_without_ReturnToDefault()
    {
        // D: Already on default - ordinary non-mutating Sync, no special default-branch path.
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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse(branch: "main", incoming: 5));
        ctx.AgentBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

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
        Assert.Contains(ctx.AgentBridge.Calls, c => c.Command == "SyncRepository");
        AssertNoWorkspaceMutationCommands(ctx.AgentBridge);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("main", link.BranchName);
        Assert.Equal(5, link.IncomingCommits);
        Assert.True(link.BranchHasUpstream);

        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Remove_when_worktree_delete_fails_does_not_report_success_or_refresh_workspace()
    {
        // E: Worktree deletion failure -> NeedsRepair; no branch delete; no post-success refresh.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureAsync(ctx);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(
            AgentHubMethods.RemoveGitWorktree,
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
        AssertNoWorkspaceMutationCommands(ctx.AgentBridge);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "SyncRepository");
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "DeleteBranch");

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.WorkspaceFeatureContexts.AnyAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value));
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.Name == "feat-refresh" && f.WorkspaceId == ctx.WorkspaceId);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);
    }

    [Fact]
    public async Task Remove_cleans_all_repositories_and_issues_per_repo_agent_commands()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureWithTwoReposAsync(ctx);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse(branch: "main", incoming: 1));
        ctx.AgentBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

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
        Assert.Equal(2, ctx.AgentBridge.Calls.Count(c => c.Command == AgentHubMethods.RemoveGitWorktree));
        Assert.Equal(2, ctx.AgentBridge.Calls.Count(c => c.Command == "DeleteBranch"));
        Assert.Contains(ctx.AgentBridge.Calls, c => c.Command == "SyncRepository");
        AssertNoWorkspaceMutationCommands(ctx.AgentBridge);
        await AssertFeatureGoneAsync(ctx, featureContextId);
    }

    [Fact]
    public async Task Remove_partial_worktree_failure_still_cleans_successful_repos_and_keeps_Feature()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedRemovableFeatureWithTwoReposAsync(ctx);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        var removeAttempts = 0;
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, _ =>
        {
            var attempt = Interlocked.Increment(ref removeAttempts);
            if (attempt == 1)
                return new AgentCommandResponse(false, null, "Permission denied on first repo");
            return new AgentCommandResponse(true, new { success = true }, null);
        });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });

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
        Assert.Equal(2, ctx.AgentBridge.Calls.Count(c => c.Command == AgentHubMethods.RemoveGitWorktree));
        // Successful repo still deletes its Feature branch; failed repo skips DeleteBranch.
        Assert.Equal(1, ctx.AgentBridge.Calls.Count(c => c.Command == "DeleteBranch"));
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "SyncRepository");

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
        await AssertAnalyzeNotAutomaticallySafeAsync(ctx, DirtyGitChangeStatus(uncommitted: true));
    }

    [Fact]
    public async Task Analyze_staged_Feature_worktree_is_not_automatically_safe()
    {
        // G
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await AssertAnalyzeNotAutomaticallySafeAsync(ctx, DirtyGitChangeStatus(staged: true));
    }

    [Fact]
    public async Task Analyze_conflicted_Feature_worktree_is_not_automatically_safe()
    {
        // H
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await AssertAnalyzeNotAutomaticallySafeAsync(ctx, DirtyGitChangeStatus(conflicted: true));
    }

    [Fact]
    public async Task Analyze_status_failure_is_not_automatically_safe()
    {
        // I
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond("GetGitChangeStatus", data: null, success: false, error: "status failed");

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
        // GetGitChangeStatus failed: not automatically safe, but dirty flags stay false so the dialog
        // shows no discard checkbox and enables Remove. The server must not demand authorization.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond("GetGitChangeStatus", data: null, success: false, error: "status failed");
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse());
        ctx.AgentBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

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
        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());

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
    public async Task Analyze_Feature_worktree_is_not_automatically_safe_when_Agent_cannot_confirm_disk_state()
    {
        // Docker / disconnected-Worker: InspectWorktree cannot be reached, so the repository is
        // Unknown, not Missing - it must not be treated as automatically safe to remove.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.AgentBridge.IsAgentConnected = false;

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

        Assert.False(result.Success);
        Assert.Contains("Could not check", result.Error);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == AgentHubMethods.RemoveGitWorktree);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "DeleteBranch");
    }

    [Fact]
    public async Task Analyze_Feature_worktree_missing_per_Agent_is_shown_as_missing_not_unknown()
    {
        // Fake Agent says missing: existing behaviour (Missing, not Unknown) is kept.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree(exists: false, isDirty: null));

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
    public async Task Analyze_Feature_worktree_reports_Agent_exists_and_dirty_even_when_path_is_not_on_the_App_host()
    {
        // Docker topology: the App never reads this path from local disk, so a path under a
        // nonexistent drive on the test machine must still be reported exactly as the Agent says.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"Z:\nope\does-not-exist\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree(exists: true, isDirty: true));
        ctx.AgentBridge.Respond("GetGitChangeStatus", DirtyGitChangeStatus(uncommitted: true));

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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());
        ctx.AgentBridge.Respond(
            AgentHubMethods.RemoveGitWorktree,
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
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "SyncRepository");
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "DeleteBranch");
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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());

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
    public async Task Analyze_live_outgoing_commits_without_pull_request_is_not_completed_or_safe()
    {
        // F-9/U-18: live commits ahead of the default branch with no pull request yet must not be
        // treated as "Completed, never opened" - the branch is pushed and awaiting review.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedFeatureWithWorktreeNoPrAsync(ctx, worktreePath);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree(aheadOfUpstream: 2, aheadOfDefault: 2));
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());

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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree(aheadOfUpstream: null));
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());

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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, new { exists = true, isDirty = false });
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());

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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());

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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse());
        ctx.AgentBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree(aheadOfUpstream: null));
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse());
        ctx.AgentBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse());
        ctx.AgentBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });

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
    public async Task Remove_refuses_dirty_worktree_without_discard_authorization()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree(isDirty: true));
        ctx.AgentBridge.Respond("GetGitChangeStatus", DirtyGitChangeStatus(uncommitted: true));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions());

        Assert.False(result.Success);
        Assert.Contains("uncommitted changes", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == AgentHubMethods.RemoveGitWorktree);
    }

    [Fact]
    public async Task Remove_refuses_unmerged_branch_without_force_authorization()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedFeatureWithWorktreeNoPrAsync(ctx, worktreePath);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree(aheadOfUpstream: 2, aheadOfDefault: 2));
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions());

        Assert.False(result.Success);
        Assert.Contains("force-deleting", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == AgentHubMethods.RemoveGitWorktree);
    }

    // ---- 09 SB-2: analysis must judge the Feature branch, not the current checkout -----------------

    [Fact]
    public async Task Analyze_drift_with_unmerged_Feature_branch_is_not_safe_and_describes_the_kept_branch()
    {
        // The worktree has been switched to "side" in a terminal; the Feature branch itself still has
        // 2 commits not on default. The analysis must judge the Feature branch, not "side".
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedFeatureWithWorktreeNoPrAsync(ctx, worktreePath);

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, new
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
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());

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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, new
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
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());

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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, new
        {
            exists = true,
            isDirty = false,
            hasUpstream = true,
            aheadOfUpstream = 0,
            aheadOfDefault = 0,
        });
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());

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

        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond("GetGitChangeStatus", CleanGitChangeStatus());

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

    private static async Task AssertAnalyzeNotAutomaticallySafeAsync(SyncStateTestContext ctx, object statusResponse)
    {
        var worktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-refresh\graymoon-api";
        var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond("GetGitChangeStatus", statusResponse);

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var plan = await ops.AnalyzeRemoveFeatureAsync(featureContextId);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(RemoveFeatureClassification.Completed, plan.Classification);
        Assert.False(plan.IsAutomaticallySafe);
        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.LiveStatusEstablished);
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
