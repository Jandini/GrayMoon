using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceHookContextAttributorTests
{
    [Fact]
    public async Task Null_path_resolves_to_special_Workspace()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var attributor = scope.ServiceProvider.GetRequiredService<IWorkspaceHookContextAttributor>();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();

        var special = await resolver.GetOrCreateSpecialWorkspaceContextIdAsync(ctx.WorkspaceId);
        var resolved = await attributor.ResolveAsync(ctx.WorkspaceId, ctx.RepositoryId, repositoryPath: null);

        Assert.Equal(special, resolved);
    }

    [Fact]
    public async Task Unknown_path_returns_null()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var attributor = scope.ServiceProvider.GetRequiredService<IWorkspaceHookContextAttributor>();

        var resolved = await attributor.ResolveAsync(
            ctx.WorkspaceId,
            ctx.RepositoryId,
            repositoryPath: @"C:\not-a-known-worktree\repo");

        Assert.Null(resolved);
    }

    [Fact]
    public async Task Feature_worktree_path_resolves_to_Feature_context()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attributor = scope.ServiceProvider.GetRequiredService<IWorkspaceHookContextAttributor>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feat-a",
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var featureContext = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow
        };
        db.WorkspaceFeatureContexts.Add(featureContext);
        await db.SaveChangesAsync();

        const string worktreePath = @"C:\gm-features\feat-a\graymoon-api";
        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = featureContext.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            WorktreePath = worktreePath,
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow,
            State = WorkspaceFeatureRepositoryState.Ready
        });
        await db.SaveChangesAsync();

        var resolved = await attributor.ResolveAsync(ctx.WorkspaceId, ctx.RepositoryId, worktreePath);
        Assert.NotNull(resolved);
        Assert.Equal(featureContext.WorkspaceFeatureContextId, resolved!.Value.Value);
    }

    [Fact]
    public async Task SyncCommand_skips_write_for_unknown_path()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        await handler.HandleAsync(new Abstractions.Notifications.RepositorySyncNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            RepositoryPath = @"C:\unknown\path",
            Version = "9.9.9",
            Branch = "unknown-branch"
        });

        var link = await ctx.ReadLinkAsync();
        Assert.NotEqual("9.9.9", link.GitVersion);
        Assert.DoesNotContain(ctx.Broadcasts, b => b.Method == "WorkspaceSynced");
    }
}
