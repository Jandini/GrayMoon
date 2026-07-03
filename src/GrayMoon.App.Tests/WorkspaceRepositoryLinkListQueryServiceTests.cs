using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
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
}
