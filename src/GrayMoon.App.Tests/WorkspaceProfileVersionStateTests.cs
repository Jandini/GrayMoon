using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Git;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Version state is three-valued: not applicable, provider failed, resolved. These cover the two places the
/// App gets that wrong without a profile - the Feature-only default-tip path, which launched GitVersion for
/// every merged pull request, and the sync-status poll, which called an absent version a mismatch.
/// </summary>
public sealed class WorkspaceProfileVersionStateTests
{
    private static object SyncResponse(string version) => new
    {
        success = true,
        version,
        branch = "feature/x",
        defaultBranch = "main",
        outgoingCommits = 0,
        incomingCommits = 0,
        defaultBranchBehind = 0,
        defaultBranchAhead = 0,
        hasUpstream = true,
        upstreamProbed = true,
        localBranches = new[] { "feature/x" },
        remoteBranches = new[] { "origin/main" },
        tags = Array.Empty<string>(),
        projects = (object?)null,
    };

    [Fact]
    public async Task Basic_feature_context_runs_no_gitversion_for_a_merged_pull_request()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedFeatureWithMergedPullRequestAsync(ctx);
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse("-"));
        ctx.WorkerBridge.Respond("GetGitVersionAtDefaultTip", new { success = true, version = "9.9.9", defaultBranch = "main" });

        await SyncAsync(ctx, featureContextId);

        Assert.DoesNotContain(ctx.WorkerBridge.Calls, call => call.Command == "GetGitVersionAtDefaultTip");
        Assert.Null(await ReadContextVersionAsync(ctx, featureContextId));
    }

    [Fact]
    public async Task GitVersion_feature_context_still_takes_its_version_from_the_default_tip()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var featureContextId = await SeedFeatureWithMergedPullRequestAsync(ctx);
        await SetProfileAsync(ctx, WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion);
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse("2.0.0"));
        ctx.WorkerBridge.Respond("GetGitVersionAtDefaultTip", new { success = true, version = "9.9.9", defaultBranch = "main" });

        await SyncAsync(ctx, featureContextId);

        Assert.Contains(ctx.WorkerBridge.Calls, call => call.Command == "GetGitVersionAtDefaultTip");
        Assert.Equal("9.9.9", await ReadContextVersionAsync(ctx, featureContextId));
    }

    [Fact]
    public async Task Workspace_without_versioning_is_in_sync_when_the_worker_probed_no_version()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("GetRepositoryVersion", new
        {
            exists = true,
            version = (string?)null,
            branch = "feature/x",
            versionProbed = false,
        });

        var statuses = await GetRepoSyncStatusAsync(ctx);

        Assert.Equal(RepoSyncStatus.InSync, statuses[ctx.RepositoryId]);
    }

    [Fact]
    public async Task Workspace_without_versioning_is_still_out_of_sync_when_the_branch_moved()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("GetRepositoryVersion", new
        {
            exists = true,
            version = (string?)null,
            branch = "some-other-branch",
            versionProbed = false,
        });

        var statuses = await GetRepoSyncStatusAsync(ctx);

        // Suppressing the version comparison must not suppress the branch comparison with it.
        Assert.Equal(RepoSyncStatus.VersionMismatch, statuses[ctx.RepositoryId]);
    }

    [Fact]
    public async Task Versioning_workspace_whose_provider_failed_is_still_reported_as_a_version_problem()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SetProfileAsync(ctx, WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion);
        ctx.WorkerBridge.Respond("GetRepositoryVersion", new
        {
            exists = true,
            version = (string?)null,
            branch = "feature/x",
            versionProbed = true,
        });

        var statuses = await GetRepoSyncStatusAsync(ctx);

        Assert.Equal(RepoSyncStatus.VersionMismatch, statuses[ctx.RepositoryId]);
    }

    [Fact]
    public async Task Response_from_a_worker_without_the_probed_flag_keeps_todays_behaviour()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        // A worker that predates workspace profiles always ran a version provider and says nothing about it,
        // so an empty version stays a mismatch even for a workspace that does not version its repositories.
        ctx.WorkerBridge.Respond("GetRepositoryVersion", new
        {
            exists = true,
            version = (string?)null,
            branch = "feature/x",
        });

        var statuses = await GetRepoSyncStatusAsync(ctx);

        Assert.Equal(RepoSyncStatus.VersionMismatch, statuses[ctx.RepositoryId]);
    }

    [Fact]
    public async Task Resolved_version_that_matches_the_persisted_one_is_in_sync()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SetProfileAsync(ctx, WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion);
        ctx.WorkerBridge.Respond("GetRepositoryVersion", new
        {
            exists = true,
            version = "1.0.0",
            branch = "feature/x",
            versionProbed = true,
        });

        var statuses = await GetRepoSyncStatusAsync(ctx);

        Assert.Equal(RepoSyncStatus.InSync, statuses[ctx.RepositoryId]);
    }

    private static async Task<IReadOnlyDictionary<int, RepoSyncStatus>> GetRepoSyncStatusAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var special = await ctx.GetSpecialContextIdAsync();
        return await git.GetRepoSyncStatusAsync(ctx.WorkspaceId, special);
    }

    private static async Task SyncAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        await git.SyncAsync(ctx.WorkspaceId, contextId);
    }

    private static async Task<string?> ReadContextVersionAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == contextId.Value
                        && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId)
            .Select(s => s.GitVersion)
            .FirstOrDefaultAsync();
    }

    private static async Task SetProfileAsync(SyncStateTestContext ctx, WorkspaceType type, WorkspaceVersioningMode versioningMode)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var workspace = await db.Workspaces.FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        workspace.Type = type;
        workspace.VersioningMode = versioningMode;
        await db.SaveChangesAsync();
    }

    /// <summary>A Feature whose only repository has a merged pull request - the one shape that reaches the default-tip path.</summary>
    private static async Task<WorkspaceFeatureContextId> SeedFeatureWithMergedPullRequestAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Sync reconciles a Feature's pull request projection against GitHub before the default-tip step runs,
        // and a reconcile that reaches GitHub at all overwrites the seeded merged row with the nothing this
        // test harness can return. Rate-limiting the connector makes the reconcile report failure instead,
        // which leaves the projection untouched - the same trick RemoveFeatureWorkspaceRefreshTests uses.
        scope.ServiceProvider
            .GetRequiredService<IGitHubRateLimitTracker>()
            .PauseUntil("github-prod", DateTimeOffset.UtcNow.AddMinutes(5));

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feat-versioning",
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
            WorktreePath = @"C:\Users\test\.graymoon\test-ws\features\feat-versioning\graymoon-api",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow,
        });
        db.WorkspaceRepositoryContextPullRequests.Add(new WorkspaceRepositoryContextPullRequest
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            PullRequestNumber = 7,
            State = "closed",
            MergedAt = DateTimeOffset.UtcNow,
            LastCheckedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}
