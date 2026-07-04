using GrayMoon.App.Services.Queries;

namespace GrayMoon.App.Tests;

public class WorkspaceProjectListQueryServiceTests
{
    [Fact]
    public async Task First_chunk_is_bounded_for_workspace()
    {
        await using var ctx = await ListQueryTestContext.CreateAsync();
        var workspaceId = ctx.DbContext.Workspaces.Select(w => w.WorkspaceId).First();
        var page = await ctx.ProjectQuery.GetPageAsync(
            new WorkspaceProjectListRequest(workspaceId, null, 50, null));

        Assert.True(page.Items.Count <= 50);
        Assert.All(page.Items, p => Assert.False(string.IsNullOrWhiteSpace(p.ProjectName)));
    }

    [Fact]
    public async Task Search_filters_projects()
    {
        await using var ctx = await ListQueryTestContext.CreateAsync();
        var workspaceId = ctx.DbContext.Workspaces.Select(w => w.WorkspaceId).First();
        var count = await ctx.ProjectQuery.CountAsync(new WorkspaceProjectListFilter(workspaceId, "csproj"));
        Assert.True(count > 0);

        var page = await ctx.ProjectQuery.GetPageAsync(
            new WorkspaceProjectListRequest(workspaceId, "csproj", 50, null));
        Assert.True(page.Items.Count > 0);
        Assert.All(page.Items, p =>
            Assert.Contains("csproj", $"{p.ProjectName} {p.ProjectFilePath} {p.ProjectType} {p.TargetFramework}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Empty_search_returns_total_count()
    {
        await using var ctx = await ListQueryTestContext.CreateAsync();
        var workspaceId = ctx.DbContext.Workspaces.Select(w => w.WorkspaceId).First();
        var count = await ctx.ProjectQuery.CountAsync(new WorkspaceProjectListFilter(workspaceId, null));
        Assert.Equal(10, count);
    }

    [Fact]
    public async Task No_results_for_missing_search()
    {
        await using var ctx = await ListQueryTestContext.CreateAsync();
        var workspaceId = ctx.DbContext.Workspaces.Select(w => w.WorkspaceId).First();
        var count = await ctx.ProjectQuery.CountAsync(new WorkspaceProjectListFilter(workspaceId, "zzzz-not-found"));
        Assert.Equal(0, count);
    }
}
