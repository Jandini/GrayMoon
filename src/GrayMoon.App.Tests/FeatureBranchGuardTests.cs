using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services.Features;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// I3: a Feature keeps every repository on its Feature branch. The guard and the three branch operations
/// refuse the actions that would break that rule; the special Workspace is untouched (R-I3a, R-I3b).
/// </summary>
public sealed class FeatureBranchGuardTests
{
    private const string FeatureName = "feat-a";

    [Fact]
    public async Task Special_workspace_context_is_allowed_without_any_feature_table_query()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        // If the guard queried the Feature repository table for the Workspace, this would throw.
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE WorkspaceFeatureRepositories");
        }

        var guard = ctx.Resolve<IFeatureBranchGuard>();
        foreach (var action in Enum.GetValues<FeatureBranchAction>())
            Assert.Null(await guard.CheckAsync(special, ctx.RepositoryId, action, "anything", isTag: false));
    }

    [Fact]
    public async Task Unknown_context_is_allowed()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var guard = ctx.Resolve<IFeatureBranchGuard>();

        Assert.Null(await guard.CheckAsync(new WorkspaceFeatureContextId(9999), ctx.RepositoryId, FeatureBranchAction.CreateBranch, "x", isTag: false));
    }

    [Fact]
    public async Task Feature_context_refuses_a_disallowed_action_and_allows_its_own_branch()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContext = await SeedFeatureAsync(ctx, pinnedTag: null);
        var guard = ctx.Resolve<IFeatureBranchGuard>();

        Assert.Null(await guard.CheckAsync(featureContext, ctx.RepositoryId, FeatureBranchAction.Checkout, FeatureName, isTag: false));
        Assert.NotNull(await guard.CheckAsync(featureContext, ctx.RepositoryId, FeatureBranchAction.Checkout, "main", isTag: false));
        Assert.Equal(FeatureBranchPolicy.CreateBranchMessage,
            await guard.CheckAsync(featureContext, ctx.RepositoryId, FeatureBranchAction.CreateBranch, "x", isTag: false));
        Assert.Equal(FeatureBranchPolicy.ReturnToDefaultMessage,
            await guard.CheckAsync(featureContext, ctx.RepositoryId, FeatureBranchAction.ReturnToDefault, "feat-a", isTag: false));
        Assert.Equal(FeatureBranchPolicy.TagNotPinnedMessage,
            await guard.CheckAsync(featureContext, ctx.RepositoryId, FeatureBranchAction.Checkout, "1.0.0", isTag: true));
    }

    [Fact]
    public async Task Feature_context_allows_a_tag_for_a_pinned_repository_and_refuses_branches()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContext = await SeedFeatureAsync(ctx, pinnedTag: "1.0.0");
        var guard = ctx.Resolve<IFeatureBranchGuard>();

        Assert.Null(await guard.CheckAsync(featureContext, ctx.RepositoryId, FeatureBranchAction.Checkout, "2.0.0", isTag: true));
        Assert.Equal(FeatureBranchPolicy.PinnedToTagMessage,
            await guard.CheckAsync(featureContext, ctx.RepositoryId, FeatureBranchAction.Checkout, FeatureName, isTag: false));
    }

    [Fact]
    public async Task Checkout_in_a_Feature_to_another_branch_is_refused_and_never_reaches_the_Worker()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContext = await SeedFeatureAsync(ctx, pinnedTag: null);
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        var outcome = await ops.CheckoutAsync(ctx.WorkspaceId, featureContext, ctx.RepositoryId, "main", isTag: false);

        Assert.Equal(400, outcome.StatusCode);
        Assert.Contains(FeatureName, outcome.ErrorText);
        Assert.Empty(ctx.AgentBridge.Calls);
    }

    [Fact]
    public async Task Checkout_in_a_Feature_strips_origin_before_comparing_with_the_Feature_branch()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContext = await SeedFeatureAsync(ctx, pinnedTag: null);
        ctx.AgentBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = FeatureName });
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        var outcome = await ops.CheckoutAsync(ctx.WorkspaceId, featureContext, ctx.RepositoryId, "origin/" + FeatureName, isTag: false);

        Assert.True(outcome.IsSuccessStatus);
        Assert.Single(ctx.AgentBridge.Calls, c => c.Command == "CheckoutBranch");
    }

    [Fact]
    public async Task CreateBranch_and_ReturnToDefault_in_a_Feature_are_refused_and_never_reach_the_Worker()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContext = await SeedFeatureAsync(ctx, pinnedTag: null);
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        var create = await ops.CreateBranchAsync(ctx.WorkspaceId, featureContext, ctx.RepositoryId, "other", "__default__");
        var back = await ops.ReturnToDefaultAsync(ctx.WorkspaceId, featureContext, ctx.RepositoryId, FeatureName, false, false);

        Assert.Equal(400, create.StatusCode);
        Assert.Equal(FeatureBranchPolicy.CreateBranchMessage, create.ErrorText);
        Assert.Equal(400, back.StatusCode);
        Assert.Equal(FeatureBranchPolicy.ReturnToDefaultMessage, back.ErrorText);
        Assert.Empty(ctx.AgentBridge.Calls);
    }

    [Fact]
    public async Task Pinned_tag_checkout_in_a_Feature_moves_the_pin_to_the_new_tag()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContext = await SeedFeatureAsync(ctx, pinnedTag: "1.0.0");
        ctx.AgentBridge.Respond("CheckoutTag", new CheckoutTagResponse { Success = true, CurrentTag = "2.0.0" });
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        var outcome = await ops.CheckoutAsync(ctx.WorkspaceId, featureContext, ctx.RepositoryId, "2.0.0", isTag: true);

        Assert.True(outcome.IsSuccessStatus);
        Assert.Equal("2.0.0", await ReadPinnedTagAsync(ctx, featureContext));
    }

    // R-I3a: the special Workspace sends the Worker exactly what it sent before the guard existed.

    [Fact]
    public async Task Workspace_checkout_still_reaches_the_Worker_with_the_same_arguments()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "release/1" });
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        var outcome = await ops.CheckoutAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "origin/release/1", isTag: false);

        Assert.True(outcome.IsSuccessStatus);
        var call = Assert.Single(ctx.AgentBridge.Calls, c => c.Command == "CheckoutBranch");
        Assert.Equal("release/1", ReadArg(call.Args, "branchName"));
        Assert.Equal("graymoon-api", ReadArg(call.Args, "repositoryName"));
    }

    [Fact]
    public async Task Workspace_create_branch_still_reaches_the_Worker_with_the_same_arguments()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("CreateBranch", new CreateBranchResponse { Success = true, CurrentBranch = "topic" });
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        var outcome = await ops.CreateBranchAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "topic", "release/1");

        Assert.True(outcome.IsSuccessStatus);
        var call = Assert.Single(ctx.AgentBridge.Calls, c => c.Command == "CreateBranch");
        Assert.Equal("topic", ReadArg(call.Args, "newBranchName"));
        Assert.Equal("release/1", ReadArg(call.Args, "baseBranchName"));
    }

    [Fact]
    public async Task Workspace_return_to_default_still_reaches_the_Worker()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("ReturnToDefaultBranch", new ReturnToDefaultBranchResponse
        {
            Success = true,
            CurrentBranch = "main",
            DefaultBranch = "main",
            LocalBranches = ["main"],
            RemoteBranches = ["origin/main"],
            Tags = [],
            GitVersion = "2.0.0",
        });
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        var outcome = await ops.ReturnToDefaultAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "feature/x", false, true);

        Assert.True(outcome.IsSuccessStatus);
        var call = Assert.Single(ctx.AgentBridge.Calls, c => c.Command == "ReturnToDefaultBranch");
        Assert.True((bool)call.Args.GetType().GetProperty("forceDeleteLocalBranch")!.GetValue(call.Args)!);
    }

    private static object? ReadArg(object args, string name) => args.GetType().GetProperty(name)!.GetValue(args);

    private static async Task<string?> ReadPinnedTagAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Where(r => r.WorkspaceFeatureContextId == contextId.Value)
            .Select(r => r.PinnedTag)
            .SingleAsync();
    }

    private static async Task<WorkspaceFeatureContextId> SeedFeatureAsync(SyncStateTestContext ctx, string? pinnedTag)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = FeatureName,
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();

        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            WorktreePath = @"C:\gm-test-root\.graymoon\test-ws\features\feat-a\graymoon-api",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
            PinnedTag = pinnedTag,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}
