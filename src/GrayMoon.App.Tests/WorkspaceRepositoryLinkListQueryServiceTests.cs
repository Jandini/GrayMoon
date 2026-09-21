using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

public class WorkspaceRepositoryLinkListQueryServiceTests
{
    [Fact]
    public async Task First_and_next_page_have_no_gaps_or_duplicates()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(120);
        await using (ctx)
        {
            const int pageSize = 50;
            var allIds = new List<int>();
            WorkspaceRepositoryLinkListCursor? cursor = null;

            for (var pageIndex = 0; pageIndex < 5; pageIndex++)
            {
                var page = await ctx.WorkspaceRepoLinkQuery.GetPageAsync(
                    new WorkspaceRepositoryLinkListRequest(workspaceId, null, pageSize, cursor));
                allIds.AddRange(page.Items.Select(i => i.WorkspaceRepositoryId));
                if (!page.HasMore)
                {
                    break;
                }

                cursor = page.NextCursor;
                Assert.NotNull(cursor);
            }

            Assert.Equal(allIds.Count, allIds.Distinct().Count());
            var total = await ctx.WorkspaceRepoLinkQuery.CountAsync(new WorkspaceRepositoryLinkListFilter(workspaceId, null));
            Assert.Equal(total, allIds.Count);
        }
    }

    [Fact]
    public async Task Search_filters_by_branch_name()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(60);
        await using (ctx)
        {
            var count = await ctx.WorkspaceRepoLinkQuery.CountAsync(new WorkspaceRepositoryLinkListFilter(workspaceId, "main"));
            Assert.True(count > 0);

            var page = await ctx.WorkspaceRepoLinkQuery.GetPageAsync(
                new WorkspaceRepositoryLinkListRequest(workspaceId, "main", 50, null));
            Assert.True(page.Items.Count > 0);
            Assert.All(page.Items, item =>
                Assert.True(
                    (item.BranchName ?? string.Empty).Contains("main", StringComparison.OrdinalIgnoreCase)
                    || (item.DefaultBranchName ?? string.Empty).Contains("main", StringComparison.OrdinalIgnoreCase)
                    || item.RepositoryName.Contains("main", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public async Task Count_matches_filter()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(30);
        await using (ctx)
        {
            var count = await ctx.WorkspaceRepoLinkQuery.CountAsync(new WorkspaceRepositoryLinkListFilter(workspaceId, null));
            Assert.Equal(30, count);
        }
    }

    [Fact]
    public async Task Header_state_reflects_seeded_workspace()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(30);
        await using (ctx)
        {
            var header = await ctx.WorkspaceRepoLinkQuery.GetHeaderStateAsync(workspaceId);
            Assert.Equal(30, header.TotalCount);
            Assert.True(header.HasUnmatchedDependencies);
            Assert.True(header.IsPushRecommended);
        }
    }

    [Fact]
    public async Task GetRepositoryIdsAtLevel_returns_all_at_level()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(60);
        await using (ctx)
        {
            var level = 2;
            var ids = await ctx.WorkspaceRepoLinkQuery.GetRepositoryIdsAtLevelAsync(workspaceId, level, null);
            var expected = await ctx.DbContext.WorkspaceRepositories
                .Where(wr => wr.WorkspaceId == workspaceId && wr.DependencyLevel == level)
                .Select(wr => wr.RepositoryId)
                .ToListAsync();
            Assert.Equal(expected.OrderBy(x => x), ids.OrderBy(x => x));
        }
    }

    [Fact]
    public async Task GetAllSnapshots_returns_full_workspace()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(25);
        await using (ctx)
        {
            var snapshots = await ctx.WorkspaceRepoLinkQuery.GetAllSnapshotsAsync(workspaceId);
            Assert.Equal(25, snapshots.Count);
            Assert.All(snapshots, s => Assert.False(string.IsNullOrWhiteSpace(s.RepositoryName)));
        }
    }

    [Fact]
    public async Task Search_sync_badge_text()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(30);
        await using (ctx)
        {
            var count = await ctx.WorkspaceRepoLinkQuery.CountAsync(new WorkspaceRepositoryLinkListFilter(workspaceId, "in sync"));
            Assert.True(count > 0);
        }
    }

    [Fact]
    public async Task GetIndex_matches_count_and_order_of_pages()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(80);
        await using (ctx)
        {
            var filter = new WorkspaceRepositoryLinkListFilter(workspaceId, null);
            var index = await ctx.WorkspaceRepoLinkQuery.GetIndexAsync(filter);
            Assert.Equal(80, index.Count);

            var page = await ctx.WorkspaceRepoLinkQuery.GetPageAsync(
                new WorkspaceRepositoryLinkListRequest(workspaceId, null, 50, null));
            Assert.Equal(
                page.Items.Select(i => i.WorkspaceRepositoryId),
                index.Take(page.Items.Count).Select(i => i.WorkspaceRepositoryId));
        }
    }

    [Fact]
    public async Task GetByIds_returns_requested_rows_in_request_order()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(40);
        await using (ctx)
        {
            var index = await ctx.WorkspaceRepoLinkQuery.GetIndexAsync(new WorkspaceRepositoryLinkListFilter(workspaceId, null));
            var ids = index.Skip(5).Take(7).Select(i => i.WorkspaceRepositoryId).Reverse().ToList();
            var rows = await ctx.WorkspaceRepoLinkQuery.GetByIdsAsync(workspaceId, ids);
            Assert.Equal(ids, rows.Select(r => r.WorkspaceRepositoryId).ToList());
            Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.RepositoryName)));
        }
    }

    [Fact]
    public async Task Snapshot_and_mapper_round_trip_archived_flag()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(8);
        await using (ctx)
        {
            var archived = await ctx.DbContext.Repositories.AsTracking().FirstAsync();
            archived.Archived = true;
            await ctx.DbContext.SaveChangesAsync();

            var snapshot = await ctx.WorkspaceRepoLinkQuery.GetSnapshotAsync(workspaceId, archived.RepositoryId);
            Assert.NotNull(snapshot);
            Assert.True(snapshot.Archived);

            var link = WorkspaceRepositoryLinkListMapper.ToLink(snapshot);
            Assert.True(link.Repository!.Archived);

            var notArchived = await ctx.DbContext.Repositories
                .AsNoTracking()
                .FirstAsync(r => r.RepositoryId != archived.RepositoryId);
            var other = await ctx.WorkspaceRepoLinkQuery.GetSnapshotAsync(workspaceId, notArchived.RepositoryId);
            Assert.NotNull(other);
            Assert.False(other.Archived);
            Assert.False(WorkspaceRepositoryLinkListMapper.ToLink(other).Repository!.Archived);
        }
    }

    [Fact]
    public async Task Snapshot_uncommitted_count_is_unique_paths_for_staged_changed_and_mixed()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(8);
        await using (ctx)
        {
            var links = await ctx.DbContext.WorkspaceRepositories
                .AsNoTracking()
                .Where(wr => wr.WorkspaceId == workspaceId)
                .OrderBy(wr => wr.WorkspaceRepositoryId)
                .Take(3)
                .ToListAsync();
            Assert.Equal(3, links.Count);

            var stagedOnly = links[0];
            var changedOnly = links[1];
            var mixed = links[2];

            ctx.DbContext.WorkspaceGitChangeEntries.AddRange(
                Entry(stagedOnly.WorkspaceRepositoryId, "src/a.cs", GitChangeKind.Modified, GitChangeKind.None),
                Entry(stagedOnly.WorkspaceRepositoryId, "src/b.cs", GitChangeKind.Added, GitChangeKind.None),
                Entry(stagedOnly.WorkspaceRepositoryId, "src/c.cs", GitChangeKind.Modified, GitChangeKind.None),
                Entry(changedOnly.WorkspaceRepositoryId, "src/d.cs", GitChangeKind.None, GitChangeKind.Modified),
                Entry(changedOnly.WorkspaceRepositoryId, "src/e.cs", GitChangeKind.None, GitChangeKind.Untracked),
                Entry(mixed.WorkspaceRepositoryId, "src/both.cs", GitChangeKind.Modified, GitChangeKind.Modified),
                Entry(mixed.WorkspaceRepositoryId, "src/staged.cs", GitChangeKind.Added, GitChangeKind.None),
                Entry(mixed.WorkspaceRepositoryId, "src/changed.cs", GitChangeKind.None, GitChangeKind.Modified));
            await ctx.DbContext.SaveChangesAsync();

            var stagedSnapshot = await ctx.WorkspaceRepoLinkQuery.GetSnapshotAsync(workspaceId, stagedOnly.RepositoryId);
            Assert.NotNull(stagedSnapshot);
            Assert.Equal(3, stagedSnapshot.UncommittedChangedFileCount);
            Assert.Equal(3, WorkspaceRepositoryLinkListMapper.ToLink(stagedSnapshot).UncommittedChangedFileCount);

            var changedSnapshot = await ctx.WorkspaceRepoLinkQuery.GetSnapshotAsync(workspaceId, changedOnly.RepositoryId);
            Assert.NotNull(changedSnapshot);
            Assert.Equal(2, changedSnapshot.UncommittedChangedFileCount);

            var mixedSnapshot = await ctx.WorkspaceRepoLinkQuery.GetSnapshotAsync(workspaceId, mixed.RepositoryId);
            Assert.NotNull(mixedSnapshot);
            Assert.Equal(3, mixedSnapshot.UncommittedChangedFileCount);

            var clean = await ctx.DbContext.WorkspaceRepositories
                .AsNoTracking()
                .Where(wr => wr.WorkspaceId == workspaceId && wr.WorkspaceRepositoryId != stagedOnly.WorkspaceRepositoryId
                    && wr.WorkspaceRepositoryId != changedOnly.WorkspaceRepositoryId
                    && wr.WorkspaceRepositoryId != mixed.WorkspaceRepositoryId)
                .Select(wr => wr.RepositoryId)
                .FirstAsync();
            var cleanSnapshot = await ctx.WorkspaceRepoLinkQuery.GetSnapshotAsync(workspaceId, clean);
            Assert.NotNull(cleanSnapshot);
            Assert.Equal(0, cleanSnapshot.UncommittedChangedFileCount);
        }
    }

    [Fact]
    public async Task Feature_context_sort_keyset_and_level_grouping_use_context_state_not_shared_link()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(12);
        await using (ctx)
        {
            var contextId = await CreateFeatureContextAsync(ctx.DbContext, workspaceId, "feature-a");

            // Give every link a context state whose DependencyLevel/RepositoryType/Dependencies/GitVersion are
            // deliberately the *inverse* of the shared link's, so any code path that accidentally reads the
            // link instead of the context state produces different sort order / grouping / results.
            var links = await ctx.DbContext.WorkspaceRepositories.AsNoTracking()
                .Where(wr => wr.WorkspaceId == workspaceId)
                .ToListAsync();
            foreach (var link in links)
            {
                ctx.DbContext.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
                {
                    WorkspaceFeatureContextId = contextId.Value,
                    WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                    DependencyLevel = 99 - (link.DependencyLevel ?? 0),
                    Dependencies = 99 - (link.Dependencies ?? 0),
                    RepositoryType = link.RepositoryType == ProjectType.Service ? ProjectType.Library : ProjectType.Service,
                    GitVersion = $"context-{link.WorkspaceRepositoryId}",
                });
            }
            await ctx.DbContext.SaveChangesAsync();

            var filter = new WorkspaceRepositoryLinkListFilter(workspaceId, null);

            // GetIndexAsync/BuildSlots grouping must key off the context's level, not the link's.
            var index = await ctx.WorkspaceRepoLinkQuery.GetIndexAsync(
                filter, contextId, isSpecialWorkspace: false);
            Assert.Equal(links.Count, index.Count);
            foreach (var entry in index)
            {
                var link = links.Single(l => l.WorkspaceRepositoryId == entry.WorkspaceRepositoryId);
                Assert.Equal(99 - (link.DependencyLevel ?? 0), entry.DependencyLevel);
            }
            var expectedOrder = index.OrderByDescending(e => e.DependencyLevel ?? int.MinValue).Select(e => e.WorkspaceRepositoryId).ToList();
            Assert.Equal(expectedOrder, index.Select(e => e.WorkspaceRepositoryId).ToList());

            // GetPageAsync's sort/keyset must reach the same order using the context state.
            var page = await ctx.WorkspaceRepoLinkQuery.GetPageAsync(
                new WorkspaceRepositoryLinkListRequest(workspaceId, null, 50, null), contextId, isSpecialWorkspace: false);
            Assert.Equal(index.Select(e => e.WorkspaceRepositoryId), page.Items.Select(i => i.WorkspaceRepositoryId));

            // GetRepositoryIdsAtLevelAsync ("jump to level" / bulk level actions) must match against the
            // context's level, not the link's.
            var someLink = links[0];
            var contextLevel = 99 - (someLink.DependencyLevel ?? 0);
            var idsAtLevel = await ctx.WorkspaceRepoLinkQuery.GetRepositoryIdsAtLevelAsync(
                workspaceId, contextLevel, null, contextId, isSpecialWorkspace: false);
            Assert.Contains(someLink.RepositoryId, idsAtLevel);
            Assert.DoesNotContain(someLink.RepositoryId, await ctx.WorkspaceRepoLinkQuery.GetRepositoryIdsAtLevelAsync(
                workspaceId, someLink.DependencyLevel, null, contextId, isSpecialWorkspace: false));

            // GetGitVersionNameMapAsync must read the context's GitVersion, not the link's. The test fixture
            // reuses repository names ("graymoon-api" for every even RepositoryId), so restrict this check to
            // repos with a unique name - the map is keyed by name and can't distinguish same-named repos
            // either way, which is a pre-existing limitation unrelated to context-scoping.
            var versionMap = await ctx.WorkspaceRepoLinkQuery.GetGitVersionNameMapAsync(
                workspaceId, contextId, isSpecialWorkspace: false);
            var repoNamesById = await ctx.DbContext.Repositories.AsNoTracking()
                .ToDictionaryAsync(r => r.RepositoryId, r => r.RepositoryName);
            var namesWithSingleRepo = repoNamesById.Values
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(links.Where(l => namesWithSingleRepo.Contains(repoNamesById[l.RepositoryId])), link =>
            {
                var repoName = repoNamesById[link.RepositoryId];
                Assert.True(versionMap.TryGetValue(repoName, out var version));
                Assert.Equal($"context-{link.WorkspaceRepositoryId}", version);
            });

            // The special Workspace's own read of the same rows must still use the shared link, unaffected.
            var workspaceIndex = await ctx.WorkspaceRepoLinkQuery.GetIndexAsync(filter);
            foreach (var entry in workspaceIndex)
            {
                var link = links.Single(l => l.WorkspaceRepositoryId == entry.WorkspaceRepositoryId);
                Assert.Equal(link.DependencyLevel, entry.DependencyLevel);
            }
        }
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

    private static WorkspaceGitChangeEntry Entry(int workspaceRepositoryId, string path, GitChangeKind index, GitChangeKind worktree) =>
        new()
        {
            WorkspaceRepositoryId = workspaceRepositoryId,
            Path = path,
            IndexChange = index,
            WorktreeChange = worktree,
            IsTracked = worktree != GitChangeKind.Untracked,
        };

    [Fact]
    public async Task Header_state_aggregates_without_loading_all_columns()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(15);
        await using (ctx)
        {
            var header = await ctx.WorkspaceRepoLinkQuery.GetHeaderStateAsync(workspaceId);
            Assert.Equal(15, header.TotalCount);
            Assert.True(header.HasUnmatchedDependencies);
            Assert.True(header.IsPushRecommended);
        }
    }
}
