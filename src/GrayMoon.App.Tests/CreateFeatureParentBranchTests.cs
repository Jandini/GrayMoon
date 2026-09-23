using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>Feature creation persists ParentBranchName from the same agent HEAD snapshot as BaseCommitSha.</summary>
public sealed class CreateFeatureParentBranchTests
{
    [Fact]
    public async Task Create_captures_independent_ParentBranchName_per_repository()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        int secondRepoId;
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var connectorId = await db.Connectors.Select(c => c.ConnectorId).FirstAsync();
            var repo2 = new Repository
            {
                ConnectorId = connectorId,
                RepositoryName = "graymoon-ui",
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = "https://github.com/acme/graymoon-ui.git",
            };
            db.Repositories.Add(repo2);
            await db.SaveChangesAsync();
            secondRepoId = repo2.RepositoryId;
            db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = repo2.RepositoryId,
                GitVersion = "1.0.0",
                BranchName = "ignored",
                DefaultBranchName = "main",
                SyncStatus = RepoSyncStatus.InSync,
                RepositoryType = ProjectType.Library,
            });
            await db.SaveChangesAsync();
        }

        ctx.AgentBridge.Respond(AgentHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                ["graymoon-ui"] = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "develop",
                ["graymoon-ui"] = "release/3.0",
            },
        });
        ctx.AgentBridge.Respond(AgentHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });

        await using var opsScope = ctx.CreateScope();
        var ops = opsScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feature/mixed", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);
        Assert.True(result.Success, result.Error);

        var parents = await ops.GetParentBranchNamesByRepositoryIdAsync(result.ContextId!.Value);
        Assert.Equal("develop", parents[ctx.RepositoryId]);
        Assert.Equal("release/3.0", parents[secondRepoId]);
    }

    [Fact]
    public async Task Create_captures_ParentBranchName_and_BaseCommitSha_from_head_snapshot()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "stale-should-not-win";
            link.DefaultBranchName = "main";
        });

        ctx.AgentBridge.Respond(AgentHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "abc123def456abc123def456abc123def456abc1",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "develop",
            },
        });
        ctx.AgentBridge.Respond(AgentHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feature/payments", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.ContextId);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.WorkspaceFeatureRepositories
            .SingleAsync(r => r.WorkspaceFeatureContextId == result.ContextId!.Value.Value);

        Assert.Equal("develop", row.ParentBranchName);
        Assert.Equal("abc123def456abc123def456abc123def456abc1", row.BaseCommitSha);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c =>
            c.Command.Contains("Checkout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_persists_null_ParentBranchName_when_detached_HEAD()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "main";
            link.DefaultBranchName = "main";
        });

        ctx.AgentBridge.Respond(AgentHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "abc123def456abc123def456abc123def456abc1",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        });
        ctx.AgentBridge.Respond(AgentHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feature/detached", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.True(result.Success, result.Error);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.WorkspaceFeatureRepositories
            .SingleAsync(r => r.WorkspaceFeatureContextId == result.ContextId!.Value.Value);

        Assert.Null(row.ParentBranchName);
        Assert.Equal("abc123def456abc123def456abc123def456abc1", row.BaseCommitSha);
    }

    [Fact]
    public async Task GetParentBranchNames_maps_RepositoryId_and_ignores_special_Workspace()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        WorkspaceFeatureContextId featureContextId;
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var feature = new WorkspaceFeature
            {
                WorkspaceId = ctx.WorkspaceId,
                Name = "feature/map",
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

            db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                WorktreePath = @"C:\wt",
                BaseCommitSha = "abc123",
                ParentBranchName = "develop",
                CreatedAt = DateTime.UtcNow,
                State = WorkspaceFeatureRepositoryState.Ready,
            });
            await db.SaveChangesAsync();
            featureContextId = new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
        }

        await using var opsScope = ctx.CreateScope();
        var ops = opsScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var map = await ops.GetParentBranchNamesByRepositoryIdAsync(featureContextId);

        Assert.True(map.TryGetValue(ctx.RepositoryId, out var parent));
        Assert.Equal("develop", parent);

        var special = await ctx.GetSpecialContextIdAsync();
        var empty = await ops.GetParentBranchNamesByRepositoryIdAsync(special);
        Assert.Empty(empty);
    }

    [Fact]
    public async Task Existing_Feature_row_without_ParentBranchName_remains_valid()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "legacy-feature",
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

        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            WorktreePath = @"C:\wt",
            BaseCommitSha = "abc123",
            ParentBranchName = null,
            CreatedAt = DateTime.UtcNow,
            State = WorkspaceFeatureRepositoryState.Ready,
        });
        await db.SaveChangesAsync();

        var row = await db.WorkspaceFeatureRepositories.AsNoTracking()
            .SingleAsync(r => r.WorkspaceFeatureContextId == context.WorkspaceFeatureContextId);
        Assert.Null(row.ParentBranchName);
        Assert.Equal("abc123", row.BaseCommitSha);
    }
}
