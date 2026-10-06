using GrayMoon.App.Components.Modals;
using GrayMoon.App.Components.Pages;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// The Workspace-role link sorts first in every sort mode, is never part of a dependency level group and
/// survives keyset paging (D13). The Role column lives on the shared link row, so a Feature context must order and
/// group correctly too (AGENTS.md "Feature-context scoping").
/// </summary>
public class WorkspaceRepositoryLinkListQueryServiceRoleTests
{
    [Fact]
    public async Task Workspace_role_row_is_first_in_every_sort()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(12);
        await using (ctx)
        {
            var workspaceLink = await MarkLastLinkAsWorkspaceRoleAsync(ctx.DbContext, workspaceId, dependencyLevel: 0);
            var filter = new WorkspaceRepositoryLinkListFilter(workspaceId, null);

            // Without the leading role ordering the Workspace row (lowest level) would sort last.
            var page = await ctx.WorkspaceRepoLinkQuery.GetPageAsync(
                new WorkspaceRepositoryLinkListRequest(workspaceId, null, 50, null));
            Assert.Equal(workspaceLink.WorkspaceRepositoryId, page.Items[0].WorkspaceRepositoryId);
            Assert.Equal(WorkspaceRepositoryRole.Workspace, page.Items[0].Role);
            Assert.All(page.Items.Skip(1), i => Assert.Equal(WorkspaceRepositoryRole.Source, i.Role));

            var index = await ctx.WorkspaceRepoLinkQuery.GetIndexAsync(filter);
            Assert.Equal(workspaceLink.WorkspaceRepositoryId, index[0].WorkspaceRepositoryId);
            Assert.Equal(WorkspaceRepositoryRole.Workspace, index[0].Role);

            var snapshots = await ctx.WorkspaceRepoLinkQuery.GetAllSnapshotsAsync(workspaceId);
            Assert.Equal(workspaceLink.WorkspaceRepositoryId, snapshots[0].WorkspaceRepositoryId);

            var snapshot = await ctx.WorkspaceRepoLinkQuery.GetSnapshotAsync(workspaceId, workspaceLink.RepositoryId);
            Assert.Equal(WorkspaceRepositoryRole.Workspace, snapshot!.Role);
            Assert.Equal(WorkspaceRepositoryRole.Workspace, WorkspaceRepositoryLinkListMapper.ToLink(snapshot).Role);

            // Feature context: the context state of every link is deliberately placed so the Workspace row would
            // sort last by level; Role (shared link) must still put it first.
            var contextId = await CreateFeatureContextAsync(ctx.DbContext, workspaceId, "feature-role-first");
            await SeedContextStatesAsync(ctx.DbContext, workspaceId, contextId, workspaceLink.WorkspaceRepositoryId);

            var featurePage = await ctx.WorkspaceRepoLinkQuery.GetPageAsync(
                new WorkspaceRepositoryLinkListRequest(workspaceId, null, 50, null), contextId, isSpecialWorkspace: false);
            Assert.Equal(workspaceLink.WorkspaceRepositoryId, featurePage.Items[0].WorkspaceRepositoryId);
            Assert.Equal(WorkspaceRepositoryRole.Workspace, featurePage.Items[0].Role);

            var featureIndex = await ctx.WorkspaceRepoLinkQuery.GetIndexAsync(filter, contextId, isSpecialWorkspace: false);
            Assert.Equal(workspaceLink.WorkspaceRepositoryId, featureIndex[0].WorkspaceRepositoryId);
            Assert.Equal(featureIndex.Select(e => e.WorkspaceRepositoryId), featurePage.Items.Select(i => i.WorkspaceRepositoryId));

            var featureSnapshots = await ctx.WorkspaceRepoLinkQuery.GetAllSnapshotsAsync(workspaceId, contextId, isSpecialWorkspace: false);
            Assert.Equal(workspaceLink.WorkspaceRepositoryId, featureSnapshots[0].WorkspaceRepositoryId);
        }
    }

    [Fact]
    public async Task Workspace_role_row_is_not_in_any_level_group()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(12);
        await using (ctx)
        {
            var workspaceLink = await MarkLastLinkAsWorkspaceRoleAsync(ctx.DbContext, workspaceId, dependencyLevel: 0);

            // Bulk level actions ("Sync level 0") must not pick the Workspace repository up.
            var idsAtLevel0 = await ctx.WorkspaceRepoLinkQuery.GetRepositoryIdsAtLevelAsync(workspaceId, 0, null);
            Assert.NotEmpty(idsAtLevel0);
            Assert.DoesNotContain(workspaceLink.RepositoryId, idsAtLevel0);

            // Slot layout: its own header first, the level headers count Source rows only.
            var index = await ctx.WorkspaceRepoLinkQuery.GetIndexAsync(new WorkspaceRepositoryLinkListFilter(workspaceId, null));
            var slots = WorkspaceRepositories.ComputeSlots(index, groupByDependencyLevel: true);

            Assert.Equal(WorkspaceRepositories.VirtualSlotKind.WorkspaceHeader, slots[0].Kind);
            Assert.Equal(1, slots[0].LevelRepoCount);
            Assert.Equal(WorkspaceRepositories.VirtualSlotKind.Row, slots[1].Kind);
            Assert.Equal(workspaceLink.WorkspaceRepositoryId, slots[1].WorkspaceRepositoryId);
            Assert.Equal(WorkspaceRepositories.VirtualSlotKind.LevelHeader, slots[2].Kind);

            var levelHeaders = slots.Where(s => s.Kind == WorkspaceRepositories.VirtualSlotKind.LevelHeader).ToList();
            Assert.Equal(index.Count - 1, levelHeaders.Sum(s => s.LevelRepoCount));
            Assert.Equal(
                index.Count(e => e.Role == WorkspaceRepositoryRole.Source && e.DependencyLevel == 0),
                levelHeaders.Single(s => s.LevelKey == 0).LevelRepoCount);
            Assert.Equal(index.Count, slots.Count(s => s.Kind == WorkspaceRepositories.VirtualSlotKind.Row));

            // Feature context: same exclusion against the context state's level.
            var contextId = await CreateFeatureContextAsync(ctx.DbContext, workspaceId, "feature-role-level");
            await SeedContextStatesAsync(ctx.DbContext, workspaceId, contextId, workspaceLink.WorkspaceRepositoryId);
            var featureIdsAtWorkspaceLevel = await ctx.WorkspaceRepoLinkQuery.GetRepositoryIdsAtLevelAsync(
                workspaceId, 0, null, contextId, isSpecialWorkspace: false);
            Assert.DoesNotContain(workspaceLink.RepositoryId, featureIdsAtWorkspaceLevel);
        }
    }

    [Fact]
    public async Task Workspace_role_row_with_no_level_is_not_in_the_no_dependencies_group()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(12);
        await using (ctx)
        {
            var workspaceLink = await MarkLastLinkAsWorkspaceRoleAsync(ctx.DbContext, workspaceId, dependencyLevel: null);

            var idsWithoutLevel = await ctx.WorkspaceRepoLinkQuery.GetRepositoryIdsAtLevelAsync(workspaceId, null, null);

            Assert.DoesNotContain(workspaceLink.RepositoryId, idsWithoutLevel);
        }
    }

    [Fact]
    public async Task Keyset_paging_keeps_workspace_row_first()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(12);
        await using (ctx)
        {
            var workspaceLink = await MarkLastLinkAsWorkspaceRoleAsync(ctx.DbContext, workspaceId, dependencyLevel: 0);
            var filter = new WorkspaceRepositoryLinkListFilter(workspaceId, null);

            var index = await ctx.WorkspaceRepoLinkQuery.GetIndexAsync(filter);
            var expected = index.Select(e => e.WorkspaceRepositoryId).ToList();
            foreach (var pageSize in new[] { 1, 2, 5 })
            {
                var paged = await PageAllAsync(ctx, workspaceId, pageSize, null, true);
                Assert.Equal(expected, paged);
                Assert.Equal(workspaceLink.WorkspaceRepositoryId, paged[0]);
            }

            var contextId = await CreateFeatureContextAsync(ctx.DbContext, workspaceId, "feature-role-keyset");
            await SeedContextStatesAsync(ctx.DbContext, workspaceId, contextId, workspaceLink.WorkspaceRepositoryId);
            var featureIndex = await ctx.WorkspaceRepoLinkQuery.GetIndexAsync(filter, contextId, isSpecialWorkspace: false);
            var featureExpected = featureIndex.Select(e => e.WorkspaceRepositoryId).ToList();
            foreach (var pageSize in new[] { 1, 2, 5 })
            {
                var paged = await PageAllAsync(ctx, workspaceId, pageSize, contextId, false);
                Assert.Equal(featureExpected, paged);
                Assert.Equal(workspaceLink.WorkspaceRepositoryId, paged[0]);
            }
        }
    }

    [Fact]
    public async Task Keyset_cursor_carries_the_role_of_the_last_row()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(6);
        await using (ctx)
        {
            await MarkLastLinkAsWorkspaceRoleAsync(ctx.DbContext, workspaceId, dependencyLevel: 0);

            var first = await ctx.WorkspaceRepoLinkQuery.GetPageAsync(
                new WorkspaceRepositoryLinkListRequest(workspaceId, null, 1, null));
            Assert.Equal(0, first.NextCursor!.RoleSortKey);

            var second = await ctx.WorkspaceRepoLinkQuery.GetPageAsync(
                new WorkspaceRepositoryLinkListRequest(workspaceId, null, 1, first.NextCursor));
            Assert.Equal(1, second.NextCursor!.RoleSortKey);
        }
    }

    private static async Task<List<int>> PageAllAsync(
        ListQueryTestContext ctx, int workspaceId, int pageSize, WorkspaceFeatureContextId? contextId, bool isSpecialWorkspace)
    {
        var ids = new List<int>();
        WorkspaceRepositoryLinkListCursor? cursor = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var page = await ctx.WorkspaceRepoLinkQuery.GetPageAsync(
                new WorkspaceRepositoryLinkListRequest(workspaceId, null, pageSize, cursor), contextId, isSpecialWorkspace);
            ids.AddRange(page.Items.Select(i => i.WorkspaceRepositoryId));
            if (!page.HasMore)
            {
                return ids;
            }

            cursor = page.NextCursor;
            Assert.NotNull(cursor);
        }

        throw new InvalidOperationException("Paging did not terminate.");
    }

    /// <summary>Turns the link with the highest id into the Workspace-role link, at the given (shared) level.</summary>
    private static async Task<WorkspaceRepositoryLink> MarkLastLinkAsWorkspaceRoleAsync(
        AppDbContext db, int workspaceId, int? dependencyLevel)
    {
        var link = await db.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .OrderByDescending(wr => wr.WorkspaceRepositoryId)
            .FirstAsync();
        link.Role = WorkspaceRepositoryRole.Workspace;
        link.DependencyLevel = dependencyLevel;
        link.Dependencies = null;
        await db.SaveChangesAsync();
        return link;
    }

    /// <summary>
    /// Context state whose levels put the Workspace row last by level (0) and every other row above it, so only the
    /// role ordering can place it first.
    /// </summary>
    private static async Task SeedContextStatesAsync(
        AppDbContext db, int workspaceId, WorkspaceFeatureContextId contextId, int workspaceLinkId)
    {
        var links = await db.WorkspaceRepositories.AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync();
        foreach (var link in links)
        {
            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                DependencyLevel = link.WorkspaceRepositoryId == workspaceLinkId ? 0 : 1 + (link.WorkspaceRepositoryId % 3),
                Dependencies = link.WorkspaceRepositoryId % 4,
                RepositoryType = ProjectType.Library,
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<WorkspaceFeatureContextId> CreateFeatureContextAsync(
        AppDbContext db, int workspaceId, string name)
    {
        var feature = new WorkspaceFeature
        {
            WorkspaceId = workspaceId,
            Name = name,
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = workspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true,
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();
        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}

/// <summary>Pure grid layout and modal filtering for the Workspace repository (no database).</summary>
public class WorkspaceRepositoryGridLayoutTests
{
    [Fact]
    public void ComputeSlots_flat_list_puts_workspace_row_under_its_own_header_first()
    {
        var index = new[]
        {
            new WorkspaceRepositoryLinkIndexEntry(10, 100, null, WorkspaceRepositoryRole.Workspace),
            new WorkspaceRepositoryLinkIndexEntry(11, 101, 2),
            new WorkspaceRepositoryLinkIndexEntry(12, 102, 1),
        };

        var slots = WorkspaceRepositories.ComputeSlots(index, groupByDependencyLevel: false);

        Assert.Equal(
            [
                WorkspaceRepositories.VirtualSlotKind.WorkspaceHeader,
                WorkspaceRepositories.VirtualSlotKind.Row,
                WorkspaceRepositories.VirtualSlotKind.Row,
                WorkspaceRepositories.VirtualSlotKind.Row,
            ],
            slots.Select(s => s.Kind));
        Assert.Equal([10, 11, 12], slots.Where(s => s.Kind == WorkspaceRepositories.VirtualSlotKind.Row).Select(s => s.WorkspaceRepositoryId));
        Assert.Equal([0, 1, 2], slots.Where(s => s.Kind == WorkspaceRepositories.VirtualSlotKind.Row).Select(s => s.StripeIndex));
    }

    [Fact]
    public void ComputeSlots_without_a_workspace_row_has_no_workspace_header()
    {
        var index = new[]
        {
            new WorkspaceRepositoryLinkIndexEntry(11, 101, 2),
            new WorkspaceRepositoryLinkIndexEntry(12, 102, null),
        };

        var grouped = WorkspaceRepositories.ComputeSlots(index, groupByDependencyLevel: true);
        var flat = WorkspaceRepositories.ComputeSlots(index, groupByDependencyLevel: false);

        Assert.DoesNotContain(grouped, s => s.Kind == WorkspaceRepositories.VirtualSlotKind.WorkspaceHeader);
        Assert.DoesNotContain(flat, s => s.Kind == WorkspaceRepositories.VirtualSlotKind.WorkspaceHeader);
    }

    [Fact]
    public void ComputeSlots_workspace_row_with_no_level_does_not_create_a_no_dependencies_group()
    {
        var index = new[]
        {
            new WorkspaceRepositoryLinkIndexEntry(10, 100, null, WorkspaceRepositoryRole.Workspace),
            new WorkspaceRepositoryLinkIndexEntry(11, 101, 2),
        };

        var slots = WorkspaceRepositories.ComputeSlots(index, groupByDependencyLevel: true);

        var levelHeaders = slots.Where(s => s.Kind == WorkspaceRepositories.VirtualSlotKind.LevelHeader).ToList();
        Assert.Single(levelHeaders);
        Assert.Equal(2, levelHeaders[0].LevelKey);
    }

    [Fact]
    public void Repositories_modal_filtering_drops_excluded_ids_and_keeps_order()
    {
        IReadOnlyList<int> ids = [5, 3, 9, 1];

        Assert.Equal([5, 9, 1], WorkspaceRepositoriesModal.ExcludeRepositories(ids, new HashSet<int> { 3 }));
        Assert.Same(ids, WorkspaceRepositoriesModal.ExcludeRepositories(ids, null));
        Assert.Same(ids, WorkspaceRepositoriesModal.ExcludeRepositories(ids, new HashSet<int>()));
    }

    [Fact]
    public void Workspace_repository_choices_exclude_sources_and_sort_by_display_name()
    {
        var repositories = new (int RepositoryId, string RepositoryName, string? OrgName)[]
        {
            (1, "zeta", "acme"),
            (2, "alpha", "acme"),
            (3, "solo", null),
            (4, "source-repo", "acme"),
        };

        var choices = WorkspaceModal.BuildWorkspaceRepositoryChoices(repositories, new HashSet<int> { 4 });

        Assert.Equal(["acme/alpha", "acme/zeta", "solo"], choices.Select(c => c.DisplayName));
        Assert.Equal([2, 1, 3], choices.Select(c => c.RepositoryId));
    }

    [Fact]
    public void Workspace_repository_choices_keep_the_current_workspace_repository()
    {
        // The current Workspace repository is not a Source, so it stays in the list and an edit shows it selected.
        var repositories = new (int RepositoryId, string RepositoryName, string? OrgName)[]
        {
            (7, "ws-repo", "acme"),
            (8, "src", "acme"),
        };

        var choices = WorkspaceModal.BuildWorkspaceRepositoryChoices(repositories, new HashSet<int> { 8 });

        Assert.Equal([7], choices.Select(c => c.RepositoryId));
    }
}
