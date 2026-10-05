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
}
