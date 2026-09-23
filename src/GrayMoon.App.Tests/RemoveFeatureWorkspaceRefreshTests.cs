using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
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
        var worktreePath = Path.Combine(Path.GetTempPath(), "gm-remove-feature-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktreePath);
        try
        {
            var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
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
        finally
        {
            if (Directory.Exists(worktreePath))
                Directory.Delete(worktreePath, recursive: true);
        }
    }

    [Fact]
    public async Task Analyze_clean_completed_Feature_is_automatically_safe()
    {
        // J
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = Path.Combine(Path.GetTempPath(), "gm-remove-feature-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktreePath);
        try
        {
            var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
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
        finally
        {
            if (Directory.Exists(worktreePath))
                Directory.Delete(worktreePath, recursive: true);
        }
    }

    [Fact]
    public async Task Analyze_after_permission_denied_keeps_merged_feature_automatically_safe_when_live_clean()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var worktreePath = Path.Combine(Path.GetTempPath(), "gm-remove-feature-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktreePath);
        try
        {
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
        finally
        {
            if (Directory.Exists(worktreePath))
                Directory.Delete(worktreePath, recursive: true);
        }
    }

    private static async Task AssertAnalyzeNotAutomaticallySafeAsync(SyncStateTestContext ctx, object statusResponse)
    {
        var worktreePath = Path.Combine(Path.GetTempPath(), "gm-remove-feature-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktreePath);
        try
        {
            var featureContextId = await SeedMergedFeatureWithWorktreeAsync(ctx, worktreePath);
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
        finally
        {
            if (Directory.Exists(worktreePath))
                Directory.Delete(worktreePath, recursive: true);
        }
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

    private static async Task AssertFeatureGoneAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId featureContextId)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatureContexts.AnyAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value));
        Assert.False(await db.WorkspaceFeatures.AnyAsync(f => f.Name == "feat-refresh" && f.WorkspaceId == ctx.WorkspaceId));
    }
}
