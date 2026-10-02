using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>C1: Create Feature persists Feature + context + Pending rows together or not at all.</summary>
public sealed class CreateFeatureIntentAtomicityTests
{
    [Fact]
    public async Task Head_commits_incomplete_writes_no_Feature_row()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        // Count matches (one repo) but SHA is blank - previously passed the Count check,
        // saved Feature + context, then returned HeadCommitsIncomplete and left ghost rows.
        ctx.AgentBridge.Respond(AgentHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "   ",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "develop",
            },
        });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(
            ctx.WorkspaceId,
            "feature/incomplete-heads",
            WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(result.Success);
        Assert.Equal("HeadCommitsIncomplete", result.Condition);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatures.AnyAsync(f =>
            f.WorkspaceId == ctx.WorkspaceId && f.Name == "feature/incomplete-heads"));
        Assert.False(await db.WorkspaceFeatureContexts.AnyAsync(c =>
            c.WorkspaceId == ctx.WorkspaceId && c.Kind == WorkspaceFeatureContextKind.Feature));
        Assert.Empty(await db.WorkspaceFeatureRepositories.ToListAsync());
    }

    [Fact]
    public async Task Exception_while_saving_repo_rows_writes_no_Feature_or_context_row()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync(configureServices: services =>
        {
            services.AddScoped<IWorkspaceContextPathResolver>(sp =>
            {
                var inner = ActivatorUtilities.CreateInstance<WorkspaceContextPathResolver>(sp);
                return new ThrowingFeaturePathResolver(inner);
            });
        });

        ctx.AgentBridge.Respond(AgentHubMethods.GetHeadCommits, new
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
        ctx.AgentBridge.Respond(AgentHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(
            ctx.WorkspaceId,
            "feature/save-rollback",
            WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(result.Success);
        Assert.Equal("Exception", result.Condition);
        Assert.Contains("Simulated path resolution failure", result.Error, StringComparison.Ordinal);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatures.AnyAsync(f =>
            f.WorkspaceId == ctx.WorkspaceId && f.Name == "feature/save-rollback"));
        Assert.False(await db.WorkspaceFeatureContexts.AnyAsync(c =>
            c.WorkspaceId == ctx.WorkspaceId
            && c.Kind == WorkspaceFeatureContextKind.Feature
            && c.WorkspaceFeature != null
            && c.WorkspaceFeature.Name == "feature/save-rollback"));
        Assert.False(await db.WorkspaceFeatureRepositories.AnyAsync());
    }

    /// <summary>Throws on the first Feature-context path resolve (Pending-row build), not Workspace paths.</summary>
    private sealed class ThrowingFeaturePathResolver(IWorkspaceContextPathResolver inner) : IWorkspaceContextPathResolver
    {
        private int _featurePathCalls;

        public Task<string> GetContextRootAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
            => inner.GetContextRootAsync(contextId, cancellationToken);

        public Task<string> GetRepositoryPathAsync(
            WorkspaceFeatureContextId contextId,
            int workspaceRepositoryId,
            CancellationToken cancellationToken = default)
        {
            // Intent phase resolves Feature Pending paths before CreateGitWorktree; throw on that first call.
            if (Interlocked.Increment(ref _featurePathCalls) == 1)
                throw new InvalidOperationException("Simulated path resolution failure.");

            return inner.GetRepositoryPathAsync(contextId, workspaceRepositoryId, cancellationToken);
        }

        public Task<(string AgentWorkspaceRoot, string AgentWorkspaceFolderName)> GetAgentWorkspaceArgsAsync(
            WorkspaceFeatureContextId contextId,
            CancellationToken cancellationToken = default)
            => inner.GetAgentWorkspaceArgsAsync(contextId, cancellationToken);
    }
}
