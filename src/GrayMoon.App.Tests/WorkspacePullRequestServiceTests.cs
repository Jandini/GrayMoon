using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
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
    public async Task Context_persisted_lookup_reads_feature_table_not_workspace_table()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedFeatureWithContextPrAsync(ctx, prNumber: 42, state: "open");

        await using var scope = ctx.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>();

        var workspacePrs = await service.GetPersistedPullRequestsForWorkspaceAsync(ctx.WorkspaceId);
        Assert.False(workspacePrs.ContainsKey(ctx.RepositoryId));

        var contextPrs = await service.GetPersistedPullRequestsForWorkspaceContextAsync(ctx.WorkspaceId, featureContextId.Value);
        Assert.True(contextPrs.TryGetValue(ctx.RepositoryId, out var pr));
        Assert.NotNull(pr);
        Assert.Equal(42, pr!.Number);
        Assert.Equal("open", pr.State);
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

    private static async Task<WorkspaceFeatureContextId> SeedFeatureWithContextPrAsync(
        SyncStateTestContext ctx, int prNumber, string state)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feat-pr-lookup",
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

        db.WorkspaceRepositoryContextPullRequests.Add(new WorkspaceRepositoryContextPullRequest
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            PullRequestNumber = prNumber,
            State = state,
            LastCheckedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}
