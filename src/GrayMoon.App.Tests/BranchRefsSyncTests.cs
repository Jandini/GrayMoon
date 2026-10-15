using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// A branch created or deleted outside GrayMoon reaches the app as a sync that probed the branch lists and nothing else.
/// It must replace the persisted branch rows and leave everything about the checkouts exactly as it was.
/// </summary>
public sealed class BranchRefsSyncTests
{
    private static RepositorySyncNotification BranchRefs(SyncStateTestContext ctx, string? repositoryPath, IEnumerable<string> local, IEnumerable<string> remote) => new()
    {
        WorkspaceId = ctx.WorkspaceId,
        RepositoryId = ctx.RepositoryId,
        RepositoryPath = repositoryPath,
        State = new RepositoryStateSnapshot
        {
            LocalBranches = [.. local],
            RemoteBranches = [.. remote],
            BranchesProbed = true
        }
    };

    private static async Task<List<(string Name, bool IsRemote)>> ReadBranchRowsAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.RepositoryBranches.AsNoTracking()
            .Where(rb => rb.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId && !rb.IsTag)
            .ToListAsync();
        return rows.Select(rb => (rb.BranchName, rb.IsRemote)).OrderBy(r => r.IsRemote).ThenBy(r => r.BranchName, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Adds_created_and_removes_deleted_branch_rows()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using (var seedScope = ctx.CreateScope())
        {
            var git = seedScope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            await git.PersistBranchesAsync(
                ctx.WorkspaceRepositoryId,
                localBranches: ["main", "deleted-in-terminal"],
                remoteBranches: ["origin/main", "origin/pruned"],
                defaultBranchName: "main");
        }

        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();
        await handler.HandleAsync(BranchRefs(ctx, null, ["main", "created-in-terminal"], ["main"]));

        Assert.Equal(
            [("created-in-terminal", false), ("main", false), ("origin/main", true)],
            await ReadBranchRowsAsync(ctx));
        Assert.Contains(ctx.Broadcasts, b => b.Method == "ContextRepositorySynced");
    }

    [Fact]
    public async Task A_following_checkout_sync_keeps_the_remote_rows_the_branch_report_wrote()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        // `git checkout -b topic` in a terminal fires both hooks: the branch report first, the checkout sync seconds later.
        // The Worker lists remote branches bare in both, and both must land on the same rows.
        await handler.HandleAsync(BranchRefs(ctx, null, ["main", "topic"], ["main", "release/1"]));
        await handler.HandleAsync(new RepositorySyncNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Version = "1.0.0",
            Branch = "topic",
            RemoteBranches = ["main", "release/1"]
        });

        Assert.Equal(
            [("main", false), ("topic", false), ("origin/main", true), ("origin/release/1", true)],
            await ReadBranchRowsAsync(ctx));
    }

    [Fact]
    public async Task Leaves_checkout_state_sync_status_and_pinned_tag_alone()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(l =>
        {
            l.CheckedOutTag = "v1.0.0";
            l.HasNewerTag = true;
            l.BranchName = null;
            l.GitVersion = "1.0.0";
            l.SyncStatus = RepoSyncStatus.NeedsSync;
        });

        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();
        await handler.HandleAsync(BranchRefs(ctx, null, ["main", "created-in-terminal"], ["main"]));

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("v1.0.0", link.CheckedOutTag);
        Assert.True(link.HasNewerTag);
        Assert.Null(link.BranchName);
        Assert.Equal("1.0.0", link.GitVersion);
        // Deriving status from a report that never looked at the checkout would turn this tag checkout into Error.
        Assert.Equal(RepoSyncStatus.NeedsSync, link.SyncStatus);
    }

    [Fact]
    public async Task From_a_Feature_worktree_updates_branch_rows_without_touching_either_checkout()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(l =>
        {
            l.CheckedOutTag = "v1.0.0";
            l.BranchName = null;
        });

        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feat-refs",
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var featureContext = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow
        };
        db.WorkspaceFeatureContexts.Add(featureContext);
        await db.SaveChangesAsync();

        const string worktreePath = @"C:\gm-features\feat-refs\api";
        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = featureContext.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            WorktreePath = worktreePath,
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow,
            State = WorkspaceFeatureRepositoryState.Ready
        });
        db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = featureContext.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            BranchName = "feat-refs",
            SyncStatus = RepoSyncStatus.VersionMismatch
        });
        await db.SaveChangesAsync();

        // Refs are shared by every worktree, so a branch made inside the Feature is reported for the whole repository.
        await handler.HandleAsync(BranchRefs(ctx, worktreePath, ["main", "feat-refs", "made-in-feature"], ["main"]));

        Assert.Contains(("made-in-feature", false), await ReadBranchRowsAsync(ctx));

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("v1.0.0", link.CheckedOutTag);
        Assert.Null(link.BranchName);

        var state = await db.WorkspaceRepositoryContextStates.AsNoTracking().SingleAsync(s =>
            s.WorkspaceFeatureContextId == featureContext.WorkspaceFeatureContextId
            && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
        Assert.Equal("feat-refs", state.BranchName);
        Assert.Equal(RepoSyncStatus.VersionMismatch, state.SyncStatus);
    }
}
