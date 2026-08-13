using GrayMoon.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Characterisation tests for PR refresh. The connector in the harness carries no token, so
/// <c>GitHubPullRequestService</c> short-circuits to "no PR" without any network call - which is
/// exactly the path that must persist a cleared row rather than leaving a stale badge behind.
/// </summary>
public sealed class WorkspacePullRequestServiceTests
{
    [Fact]
    public async Task Refresh_persists_a_row_for_a_repository_with_no_pull_request()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>();

        await service.RefreshPullRequestsAsync(ctx.WorkspaceId, [ctx.RepositoryId], force: true);

        var persisted = await ctx.ReadPullRequestAsync();
        Assert.NotNull(persisted);
        Assert.Null(persisted!.PullRequestNumber);
    }

    [Fact]
    public async Task Persisted_lookup_reports_no_pull_request_for_a_cleared_row()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>();

        await service.RefreshPullRequestsAsync(ctx.WorkspaceId, [ctx.RepositoryId], force: true);

        var byRepo = await service.GetPersistedPullRequestsForWorkspaceAsync(ctx.WorkspaceId);
        Assert.True(byRepo.TryGetValue(ctx.RepositoryId, out var pr));
        Assert.Null(pr);
    }

    [Fact]
    public async Task Empty_repository_list_is_a_no_op()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>();

        await service.RefreshPullRequestsAsync(ctx.WorkspaceId, []);

        Assert.Null(await ctx.ReadPullRequestAsync());
    }
}
