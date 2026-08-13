using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Tests for the per-group replace semantics of <see cref="WorkspaceRepositoryStateWriter"/>: a group is
/// rewritten only when its probe marker says the agent actually looked, and a probed null really does clear
/// the column. The seeded row starts on <c>feature/x</c> with every badge column populated, so any accidental
/// write shows up as a changed value.
/// </summary>
public sealed class WorkspaceRepositoryStateWriterTests
{
    private static async Task<bool> ApplyAsync(
        SyncStateTestContext ctx,
        RepositoryStateSnapshot snapshot,
        RepositoryStateWriteOptions? options = null)
    {
        await using var scope = ctx.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
        return await writer.ApplyAsync(ctx.WorkspaceId, ctx.RepositoryId, snapshot, options);
    }

    [Fact]
    public async Task Snapshot_with_no_probe_markers_leaves_every_column_intact()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var before = await ctx.ReadLinkAsync();

        // What an agent that predates the snapshot contract effectively sends: a payload the App can
        // deserialize but that claims nothing. It must not be able to blank a single badge.
        var applied = await ApplyAsync(ctx, new RepositoryStateSnapshot());

        Assert.True(applied);
        var after = await ctx.ReadLinkAsync();
        Assert.Equal(before.GitVersion, after.GitVersion);
        Assert.Equal(before.BranchName, after.BranchName);
        Assert.Equal(before.CheckedOutTag, after.CheckedOutTag);
        Assert.Equal(before.DefaultBranchName, after.DefaultBranchName);
        Assert.Equal(before.OutgoingCommits, after.OutgoingCommits);
        Assert.Equal(before.IncomingCommits, after.IncomingCommits);
        Assert.Equal(before.DefaultBranchAheadCommits, after.DefaultBranchAheadCommits);
        Assert.Equal(before.DefaultBranchBehindCommits, after.DefaultBranchBehindCommits);
        Assert.Equal(before.BranchHasUpstream, after.BranchHasUpstream);
        Assert.Equal(before.Projects, after.Projects);
        Assert.Equal(before.RepositoryType, after.RepositoryType);
        Assert.Equal(before.SyncStatus, after.SyncStatus);
    }

    [Fact]
    public async Task Snapshot_with_no_probe_markers_does_not_touch_branch_rows_or_projects()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        await using (var scope = ctx.CreateScope())
        {
            var branchWriter = scope.ServiceProvider.GetRequiredService<RepositoryBranchWriter>();
            await branchWriter.PersistAsync(ctx.WorkspaceRepositoryId, ["feature/x", "main"], ["origin/main"], "main", ["v1.0.0"], null);
        }

        await ApplyAsync(ctx, new RepositoryStateSnapshot());

        var branches = await ctx.ReadBranchesAsync();
        Assert.Equal(4, branches.Count);
    }

    [Fact]
    public async Task Probed_commit_counts_replace_the_persisted_values_including_with_nulls()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        // A branch with no upstream genuinely has no outgoing or incoming count. The old conditional merge
        // kept the previous branch's numbers here, which is what left stale divergence badges on screen.
        await ApplyAsync(ctx, new RepositoryStateSnapshot
        {
            OutgoingCommits = null,
            IncomingCommits = null,
            DefaultBranchAhead = 0,
            DefaultBranchBehind = 0,
            HasUpstream = false,
            CommitCountsProbed = true,
            UpstreamProbed = true,
        });

        var after = await ctx.ReadLinkAsync();
        Assert.Null(after.OutgoingCommits);
        Assert.Null(after.IncomingCommits);
        Assert.Equal(0, after.DefaultBranchAheadCommits);
        Assert.Equal(0, after.DefaultBranchBehindCommits);
        Assert.False(after.BranchHasUpstream);
    }

    [Fact]
    public async Task Unprobed_commit_counts_are_cleared_when_the_branch_changes()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        // A plain checkout reports the new branch but no counts. The previous branch's numbers describe a
        // branch that is no longer checked out, so they must not survive until the hook catches up.
        await ApplyAsync(ctx, new RepositoryStateSnapshot { BranchName = "main", IdentityProbed = true });

        var after = await ctx.ReadLinkAsync();
        Assert.Equal("main", after.BranchName);
        Assert.Null(after.OutgoingCommits);
        Assert.Null(after.IncomingCommits);
        Assert.Null(after.DefaultBranchAheadCommits);
        Assert.Null(after.DefaultBranchBehindCommits);
        Assert.Null(after.BranchHasUpstream);
    }

    [Fact]
    public async Task Unprobed_commit_counts_survive_when_the_branch_is_unchanged()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        await ApplyAsync(ctx, new RepositoryStateSnapshot
        {
            BranchName = "feature/x",
            GitVersion = "1.1.0",
            IdentityProbed = true,
            GitVersionProbed = true,
        });

        var after = await ctx.ReadLinkAsync();
        Assert.Equal("1.1.0", after.GitVersion);
        Assert.Equal(3, after.OutgoingCommits);
        Assert.Equal(2, after.IncomingCommits);
        Assert.True(after.BranchHasUpstream);
    }

    [Fact]
    public async Task Tag_checkout_clears_the_branch_scoped_columns()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        await ApplyAsync(ctx, new RepositoryStateSnapshot
        {
            CheckedOutTag = "v2.0.0",
            OutgoingCommits = 9,
            IncomingCommits = 9,
            HasUpstream = true,
            IdentityProbed = true,
            CommitCountsProbed = true,
            UpstreamProbed = true,
        });

        var after = await ctx.ReadLinkAsync();
        Assert.Equal("v2.0.0", after.CheckedOutTag);
        Assert.Null(after.BranchName);
        // Counts reported alongside a tag are ignored: a detached HEAD has no branch to count against.
        Assert.Null(after.OutgoingCommits);
        Assert.Null(after.IncomingCommits);
        Assert.Null(after.BranchHasUpstream);
    }

    [Fact]
    public async Task Probed_empty_project_list_prunes_the_previous_branch_projects()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        await ApplyAsync(ctx, new RepositoryStateSnapshot
        {
            Projects =
            [
                new RepositorySyncProjectNotification { Name = "Api", ProjectType = (int)ProjectType.Service, ProjectPath = "src/Api/Api.csproj" }
            ],
            ProjectsProbed = true,
        });
        Assert.Single(await ctx.ReadProjectsAsync());
        Assert.Equal(ProjectType.Service, (await ctx.ReadLinkAsync()).RepositoryType);

        // The branch now checked out genuinely has no projects. An empty probed list is a real answer, so
        // the previous branch's projects and their dependency edges go away.
        await ApplyAsync(ctx, new RepositoryStateSnapshot { Projects = [], ProjectsProbed = true });

        Assert.Empty(await ctx.ReadProjectsAsync());
        var after = await ctx.ReadLinkAsync();
        Assert.Equal(0, after.Projects);
        Assert.Null(after.RepositoryType);
    }

    [Fact]
    public async Task Derived_sync_status_is_error_without_a_usable_version()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        await ApplyAsync(
            ctx,
            new RepositoryStateSnapshot { GitVersion = null, GitVersionProbed = true },
            new RepositoryStateWriteOptions { SyncStatus = SyncStatusWrite.Derive });

        Assert.Equal(RepoSyncStatus.Error, (await ctx.ReadLinkAsync()).SyncStatus);
    }

    [Fact]
    public async Task Sync_status_is_left_alone_by_default()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(l => l.SyncStatus = RepoSyncStatus.NeedsSync);

        await ApplyAsync(ctx, new RepositoryStateSnapshot { GitVersion = null, GitVersionProbed = true });

        Assert.Equal(RepoSyncStatus.NeedsSync, (await ctx.ReadLinkAsync()).SyncStatus);
    }

    [Fact]
    public async Task Moving_onto_the_default_branch_clears_the_persisted_pull_request()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        await using (var scope = ctx.CreateScope())
        {
            var prRepository = scope.ServiceProvider.GetRequiredService<GrayMoon.App.Repositories.WorkspacePullRequestRepository>();
            await prRepository.UpsertAsync(ctx.WorkspaceRepositoryId, new PullRequestInfo
            {
                Number = 7,
                State = "open",
                HtmlUrl = "https://github.com/acme/graymoon-api/pull/7",
            });
        }
        Assert.NotNull(await ctx.ReadPullRequestAsync());

        // GitHub cannot have a pull request from the default branch to itself, so the row is cleared
        // directly rather than through a call that could fail and leave the merged badge on screen.
        await ApplyAsync(
            ctx,
            new RepositoryStateSnapshot { BranchName = "main", IdentityProbed = true },
            new RepositoryStateWriteOptions { ReconcilePullRequest = true });

        var pr = await ctx.ReadPullRequestAsync();
        Assert.True(pr == null || pr.PullRequestNumber == null);
    }

    [Fact]
    public async Task Unlinked_repository_is_reported_rather_than_throwing()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        await using var scope = ctx.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
        var applied = await writer.ApplyAsync(ctx.WorkspaceId, repositoryId: 99999, new RepositoryStateSnapshot());

        Assert.False(applied);
    }
}
