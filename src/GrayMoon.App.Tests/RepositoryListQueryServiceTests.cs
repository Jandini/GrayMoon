using GrayMoon.App.Services.Queries;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

public class RepositoryListQueryServiceTests
{
    [Fact]
    public async Task First_chunk_is_bounded()
    {
        await using var ctx = await ListQueryTestContext.CreateAsync();
        var page = await ctx.RepositoryQuery.GetPageAsync(
            new RepositoryListRequest(null, null, RepositorySortField.Name, false, 50, null));

        Assert.Equal(50, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task Second_chunk_has_no_duplicates_and_continues_order()
    {
        await using var ctx = await ListQueryTestContext.CreateAsync();
        var first = await ctx.RepositoryQuery.GetPageAsync(
            new RepositoryListRequest(null, null, RepositorySortField.Name, false, 50, null));
        var second = await ctx.RepositoryQuery.GetPageAsync(
            new RepositoryListRequest(null, null, RepositorySortField.Name, false, 50, first.NextCursor));

        var firstIds = first.Items.Select(i => i.RepositoryId).ToHashSet();
        Assert.All(second.Items, item => Assert.DoesNotContain(item.RepositoryId, firstIds));

        var lastFirst = first.Items[^1];
        var firstSecond = second.Items[0];
        Assert.True(
            string.Compare(lastFirst.RepositoryName, firstSecond.RepositoryName, StringComparison.Ordinal) < 0
            || (lastFirst.RepositoryName == firstSecond.RepositoryName && lastFirst.RepositoryId < firstSecond.RepositoryId));
    }

    [Fact]
    public async Task Search_is_case_insensitive_and_matches_org()
    {
        await using var ctx = await ListQueryTestContext.CreateAsync();
        var filter = new RepositoryListFilter("ACME", null);
        var count = await ctx.RepositoryQuery.CountAsync(filter);
        var page = await ctx.RepositoryQuery.GetPageAsync(
            new RepositoryListRequest("ACME", null, RepositorySortField.Name, false, 50, null));

        Assert.True(count > 0);
        Assert.True(page.Items.Count <= 50);
        Assert.True(page.Items.Count <= count);
        Assert.All(page.Items, item =>
            Assert.True(
                (item.OrgName ?? string.Empty).Contains("acme", StringComparison.OrdinalIgnoreCase)
                || item.RepositoryName.Contains("acme", StringComparison.OrdinalIgnoreCase)
                || (item.Topics ?? string.Empty).Contains("acme", StringComparison.OrdinalIgnoreCase)
                || item.ConnectorName.Contains("acme", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Empty_search_returns_all_with_count()
    {
        await using var ctx = await ListQueryTestContext.CreateAsync();
        var count = await ctx.RepositoryQuery.CountAsync(new RepositoryListFilter("   ", null));
        Assert.Equal(120, count);
    }

    [Fact]
    public async Task Topic_field_search()
    {
        await using var ctx = await ListQueryTestContext.CreateAsync();
        var count = await ctx.RepositoryQuery.CountAsync(new RepositoryListFilter("topic:blazor", null));
        Assert.True(count > 0);
    }

    [Fact]
    public async Task RestrictToRepositoryIds_limits_results()
    {
        await using var ctx = await ListQueryTestContext.CreateAsync();
        var ids = await ctx.RepositoryQuery.GetMatchingIdsAsync(new RepositoryListFilter(null, null));
        var subset = ids.Take(3).ToList();
        var count = await ctx.RepositoryQuery.CountAsync(new RepositoryListFilter(null, subset));
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task GetMatchingIds_supports_toggle_all_semantics()
    {
        await using var ctx = await ListQueryTestContext.CreateAsync();
        var ids = await ctx.RepositoryQuery.GetMatchingIdsAsync(new RepositoryListFilter("gray", null));
        Assert.True(ids.Count > 0);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task Query_uses_take_in_sql()
    {
        await using var ctx = await ListQueryTestContext.CreateAsync();
        var query = ctx.DbContext.Repositories.AsNoTracking()
            .OrderBy(r => r.RepositoryName)
            .ThenBy(r => r.RepositoryId)
            .Select(r => new RepositoryListItemDto(
                r.RepositoryId,
                r.RepositoryName,
                r.OrgName,
                r.Connector != null ? r.Connector.ConnectorName : "Unknown",
                r.Visibility,
                r.Archived,
                r.Topics))
            .Take(51);

        var sql = query.ToQueryString();
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
    }
}
