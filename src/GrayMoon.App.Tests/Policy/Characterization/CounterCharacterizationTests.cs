using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Components.Shared;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy.Characterization;

/// <summary>
/// Phase 0A: the counters that decide which header actions and notifications are offered. The header query has four
/// projections (special Workspace or Feature, with or without the dependency graph); each is run over one repository
/// in four shapes. The notification popup computes its own sets in memory. Known differences between the two are
/// recorded at the end, so the policy extraction changes none of them by accident.
/// </summary>
public sealed class CounterCharacterizationTests
{
    private static readonly WorkspaceCapabilities Basic =
        new(WorkspaceType.Basic, WorkspaceVersioningMode.None, WorkspaceCiProvider.None);

    public enum Row
    {
        Branch,
        Tag,
        NeverPushedUpstream,
        WorkspaceRoleBranch,
    }

    public static TheoryData<bool, bool, Row> Cases()
    {
        var data = new TheoryData<bool, bool, Row>();
        foreach (var feature in new[] { false, true })
        foreach (var graph in new[] { false, true })
        foreach (var row in Enum.GetValues<Row>())
            data.Add(feature, graph, row);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Header_counters_for_one_repository(bool feature, bool dependencyGraph, Row row)
    {
        await using var ctx = await CreateAsync();
        var onTag = row == Row.Tag;
        var neverPushed = row == Row.NeverPushedUpstream;
        var special = await ctx.GetSpecialContextIdAsync();
        WorkspaceFeatureContextId contextId;

        if (feature)
        {
            // The Feature's state carries the values; the shared link is clean, so a counter can only come from the state.
            await ctx.MutateLinkAsync(link =>
            {
                link.OutgoingCommits = 0;
                link.IncomingCommits = 0;
                link.DefaultBranchAheadCommits = 0;
                link.Role = row == Row.WorkspaceRoleBranch ? WorkspaceRepositoryRole.Workspace : WorkspaceRepositoryRole.Source;
            });
            contextId = await SeedFeatureAsync(ctx, [new FeatureRepo(ctx.RepositoryId, WithState: false)]);
            await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                BranchName = onTag ? null : "feature/y",
                CheckedOutTag = onTag ? Tag : null,
                OutgoingCommits = neverPushed ? 0 : 3,
                IncomingCommits = 2,
                DefaultBranchAheadCommits = 4,
                BranchHasUpstream = !neverPushed,
                UnmatchedDeps = 1,
                OutOfDateFileRepos = 1,
                DependencyLevel = 2,
            });
            await db.SaveChangesAsync();
            // InSync is the insert sentinel of this column (an inserted InSync is stored as NeedsSync), so it is set by update.
            var state = await db.WorkspaceRepositoryContextStates.SingleAsync(s => s.WorkspaceFeatureContextId == contextId.Value);
            state.SyncStatus = RepoSyncStatus.InSync;
            await db.SaveChangesAsync();
        }
        else
        {
            contextId = special;
            await ctx.MutateLinkAsync(link =>
            {
                link.SyncStatus = RepoSyncStatus.InSync;
                link.BranchName = onTag ? null : "feature/x";
                link.CheckedOutTag = onTag ? Tag : null;
                link.OutgoingCommits = neverPushed ? 0 : 3;
                link.BranchHasUpstream = !neverPushed;
                link.UnmatchedDeps = 1;
                link.OutOfDateFileRepos = 1;
                link.DependencyLevel = 2;
                link.Role = row == Row.WorkspaceRoleBranch ? WorkspaceRepositoryRole.Workspace : WorkspaceRepositoryRole.Source;
            });
        }

        var header = await HeaderAsync(ctx, contextId, isSpecial: !feature, dependencyGraph ? WorkspaceCapabilities.Legacy : Basic);

        Assert.Equal(1, header.TotalCount);
        Assert.Equal(onTag, header.HasTaggedRepos);
        Assert.Equal(!onTag, header.IsPushRecommended);
        Assert.Equal(!onTag, header.HasIncomingCommits);
        Assert.Equal(!onTag, header.HasCreatablePr);
        Assert.Equal(!onTag && dependencyGraph, header.HasUnmatchedDependencies);
        Assert.Equal(!onTag && dependencyGraph ? 2 : null, header.LowestLevelNeedingWork);
        Assert.Equal(!onTag && !dependencyGraph, header.HasOutOfDateFiles);
        // Not tag-filtered: a tag checkout that is out of sync still raises the out-of-sync counter.
        Assert.False(header.IsOutOfSync);
        Assert.False(header.HasOpenPr);
    }

    /// <summary>F7: a Feature repository with no state row is counted in the total and as out of sync, and in nothing else.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Header_counters_for_a_Feature_repository_with_no_state_row(bool dependencyGraph)
    {
        await using var ctx = await CreateAsync();
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(ctx.RepositoryId, WithState: false)]);

        var header = await HeaderAsync(ctx, feature, isSpecial: false, dependencyGraph ? WorkspaceCapabilities.Legacy : Basic);

        Assert.Equal(1, header.TotalCount);
        Assert.True(header.IsOutOfSync);
        Assert.False(header.IsPushRecommended);
        Assert.False(header.HasIncomingCommits);
        Assert.False(header.HasTaggedRepos);
        Assert.False(header.HasCreatablePr);
        Assert.False(header.HasUnmatchedDependencies);
        Assert.Null(header.LowestLevelNeedingWork);
    }

    [Fact]
    public async Task Header_out_of_sync_counter_is_not_tag_filtered()
    {
        await using var ctx = await CreateAsync();
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        await ctx.MutateLinkAsync(link => link.SyncStatus = RepoSyncStatus.NeedsSync);

        var header = await HeaderAsync(ctx, await ctx.GetSpecialContextIdAsync(), isSpecial: true, WorkspaceCapabilities.Legacy);

        Assert.True(header.IsOutOfSync);
        Assert.True(header.HasTaggedRepos);
    }

    [Fact]
    public void Notification_lists_branch_repositories_and_never_a_tag_pinned_one()
    {
        var branch = Dto(1, "a", outgoing: 2, incoming: 1, unmatchedDeps: 1, level: 3);
        var workspaceRole = Dto(2, "b", outgoing: 1, role: WorkspaceRepositoryRole.Workspace);
        var tagged = Dto(3, "c", tag: Tag, outgoing: 5, incoming: 5, unmatchedDeps: 5, outOfDateFiles: 5, level: 1);

        var notification = WorkspacePendingActionsService.ComputeNotification(7, "ws", [branch, workspaceRole, tagged]);

        Assert.NotNull(notification);
        Assert.True(notification.HasUnmatchedDependencies);
        Assert.True(notification.IsPushRecommended);
        Assert.True(notification.HasIncomingCommits);
        Assert.Equal(3, notification.LowestLevelNeedingWork);
        Assert.Equal(2, notification.TotalActionRepoCount);
        Assert.Equal([2, 1], notification.Repos.Select(r => r.RepositoryId));
    }

    [Fact]
    public void Notification_is_absent_when_only_a_tag_pinned_repository_has_work()
    {
        var tagged = Dto(3, "c", tag: Tag, outgoing: 5, incoming: 5, unmatchedDeps: 5, outOfDateFiles: 5);
        var idle = Dto(4, "d");

        Assert.Null(WorkspacePendingActionsService.ComputeNotification(7, "ws", [tagged, idle]));
    }

    [Fact]
    public void Notification_panel_sets_skip_the_tag_pinned_repository_and_ignore_the_role()
    {
        var onDefault = Dto(1, "a", branch: "main", outgoing: 2, incoming: 1, unmatchedDeps: 1);
        var workspaceRole = Dto(2, "b", branch: "main", outgoing: 1, incoming: 1, unmatchedDeps: 1, role: WorkspaceRepositoryRole.Workspace);
        var tagged = Dto(3, "c", tag: Tag, outgoing: 5, incoming: 5, unmatchedDeps: 5, outOfDateFiles: 5);
        var links = new[] { onDefault, workspaceRole, tagged };

        Assert.Equal([1, 2], NotificationPanelRepoSets.NeedingUpdate(links).Order());
        Assert.Equal([1, 2], NotificationPanelRepoSets.WithIncoming(links));
        Assert.Equal(["a", "b"], NotificationPanelRepoSets.UpdateOnDefaultBranch(links).Select(r => r.RepositoryName));
        Assert.Equal(["a", "b"], NotificationPanelRepoSets.PushOnDefaultBranch(links).Select(r => r.RepositoryName));
        Assert.Equal([("a", 2), ("b", 1)], NotificationPanelRepoSets.WithOutgoing(links));
    }

    /// <summary>Known difference 1: the header recommends a push for a branch never pushed upstream; the notification does not.</summary>
    [Fact]
    public async Task Known_difference_header_push_counts_a_never_pushed_branch_and_the_notification_does_not()
    {
        await using var ctx = await CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.OutgoingCommits = 0;
            link.IncomingCommits = 0;
            link.BranchHasUpstream = false;
        });
        var special = await ctx.GetSpecialContextIdAsync();

        var header = await HeaderAsync(ctx, special, isSpecial: true, WorkspaceCapabilities.Legacy);
        await using var scope = ctx.CreateScope();
        var links = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>()
            .GetAllSnapshotsAsync(ctx.WorkspaceId, special, true);
        var notification = WorkspacePendingActionsService.ComputeNotification(ctx.WorkspaceId, "test-ws", links);

        Assert.True(header.IsPushRecommended);
        Assert.Null(notification);
        Assert.Empty(NotificationPanelRepoSets.WithOutgoing(links));
    }

    /// <summary>Known difference 2: an out-of-date version file alone makes a repository need an update, but is not warned about on the default branch.</summary>
    [Fact]
    public void Known_difference_needing_update_counts_out_of_date_files_and_update_on_default_branch_does_not()
    {
        var filesOnly = Dto(1, "a", branch: "main", outOfDateFiles: 1);

        Assert.Equal([1], NotificationPanelRepoSets.NeedingUpdate([filesOnly]));
        Assert.Empty(NotificationPanelRepoSets.UpdateOnDefaultBranch([filesOnly]));
    }

    /// <summary>Known difference 3: the header's creatable-PR counter does not need a verified PR state; the badge does.</summary>
    [Fact]
    public async Task Known_difference_header_creatable_pr_ignores_pr_verification_and_the_badge_requires_it()
    {
        await using var ctx = await CreateAsync();

        var header = await HeaderAsync(ctx, await ctx.GetSpecialContextIdAsync(), isSpecial: true, WorkspaceCapabilities.Legacy);

        Assert.True(header.HasCreatablePr);
        Assert.False(PRBadge.ShowsCreateBadge(isOnTag: false, isPrVerified: false, pullRequest: null, defaultBranchAheadCommits: 4));
        Assert.True(PRBadge.ShowsCreateBadge(isOnTag: false, isPrVerified: true, pullRequest: null, defaultBranchAheadCommits: 4));
        Assert.False(PRBadge.ShowsCreateBadge(isOnTag: true, isPrVerified: true, pullRequest: null, defaultBranchAheadCommits: 4));
    }

    private static async Task<WorkspaceRepositoryHeaderStateDto> HeaderAsync(
        SyncStateTestContext ctx, WorkspaceFeatureContextId contextId, bool isSpecial, WorkspaceCapabilities capabilities)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>()
            .GetHeaderStateAsync(ctx.WorkspaceId, contextId, isSpecial, capabilities);
    }
}
