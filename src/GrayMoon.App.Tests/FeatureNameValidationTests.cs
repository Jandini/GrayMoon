using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>E1: invalid names are refused and case-only duplicate names are refused.</summary>
public sealed class FeatureNameValidationTests
{
    [Fact]
    public async Task Invalid_name_is_refused_before_any_write()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(
            ctx.WorkspaceId,
            "feature with space",
            WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(result.Success);
        Assert.Equal("InvalidFeatureName", result.Condition);
        Assert.Contains("space", result.Error, StringComparison.OrdinalIgnoreCase);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == ctx.WorkspaceId));
    }

    [Fact]
    public async Task Case_only_duplicate_name_is_refused()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "develop",
            },
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });

        await using var firstScope = ctx.CreateScope();
        var firstOps = firstScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var firstResult = await firstOps.CreateFeatureAsync(
            ctx.WorkspaceId,
            "feature/CaseTest",
            WorkspaceFeatureBaseKindApplication.CurrentWorkspace);
        Assert.True(firstResult.Success, firstResult.Error);

        await using var secondScope = ctx.CreateScope();
        var secondOps = secondScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var secondResult = await secondOps.CreateFeatureAsync(
            ctx.WorkspaceId,
            "feature/casetest",
            WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(secondResult.Success);
        Assert.Equal("DuplicateName", secondResult.Condition);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.WorkspaceFeatures.CountAsync(f => f.WorkspaceId == ctx.WorkspaceId);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("team", "team/login", true)]
    [InlineData("team/login", "team", true)]
    [InlineData("Team/Login/x", "team/login", true)]
    [InlineData("team", "teammate/login", false)]
    [InlineData("team/a", "team/b", false)]
    [InlineData("team", "team", false)]
    public void Feature_names_nest_when_one_is_the_other_plus_a_slash(string first, string second, bool nests) =>
        Assert.Equal(nests, GrayMoon.App.Services.Features.WorkspaceFeatureOperations.FeatureNamesNest(first, second));

    [Theory]
    [InlineData("team", "team/login")]
    [InlineData("team/login", "team")]
    public async Task A_name_that_nests_with_an_existing_Feature_is_refused_before_any_write(string existing, string requested)
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using (var seed = ctx.CreateScope())
        {
            var seedDb = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            seedDb.WorkspaceFeatures.Add(new GrayMoon.App.Models.WorkspaceFeature
            {
                WorkspaceId = ctx.WorkspaceId,
                Name = existing,
                LifecycleState = GrayMoon.App.Models.WorkspaceFeatureLifecycleState.Ready,
                BaseKind = GrayMoon.App.Models.WorkspaceFeatureBaseKind.CurrentWorkspace,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, requested, WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(result.Success);
        Assert.Equal("NestedFeatureName", result.Condition);
        Assert.Contains($"'{existing}'", result.Error);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.WorkspaceFeatures.CountAsync(f => f.WorkspaceId == ctx.WorkspaceId));
    }
}
