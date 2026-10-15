using System.Collections.Concurrent;
using GrayMoon.Abstractions.Worker;
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
/// The per-repository worker round trips in <see cref="WorkspaceGitService"/> fan out bounded by
/// <see cref="WorkspaceOptions.MaxParallelOperations"/>: all repositories are covered, the limit is honoured,
/// and the limit is actually reached (so a regression back to one-at-a-time is caught).
/// </summary>
public sealed class WorkspaceGitServiceParallelismTests
{
    private const int ExtraRepositories = 5;

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task GetRepoSyncStatusAsync_runs_worker_probes_in_parallel_bounded_by_MaxParallelOperations(int maxParallel)
    {
        await using var ctx = await SyncStateTestContext.CreateAsync(
            configureServices: services => services.Configure<WorkspaceOptions>(o => o.MaxParallelOperations = maxParallel));
        var repositoryIds = await SeedExtraRepositoriesAsync(ctx);
        repositoryIds.Add(ctx.RepositoryId);

        var probe = new ConcurrencyProbe();
        ctx.WorkerBridge.RespondAsync("GetRepositoryVersion", async (_, ct) =>
        {
            await probe.RunAsync(ct);
            return new WorkerCommandResponse(true, new { exists = true, version = (string?)null, branch = "feature/x", versionProbed = false }, null);
        });

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var special = await ctx.GetSpecialContextIdAsync();
        var progressed = new ConcurrentBag<int>();
        var statuses = await git.GetRepoSyncStatusAsync(ctx.WorkspaceId, special, (id, _) => progressed.Add(id));

        Assert.Equal(repositoryIds.Count, statuses.Count);
        Assert.All(repositoryIds, id => Assert.Equal(RepoSyncStatus.InSync, statuses[id]));
        Assert.Equal(repositoryIds.OrderBy(i => i), progressed.OrderBy(i => i));
        probe.AssertBoundedBy(maxParallel);
    }

    [Fact]
    public async Task GetRepoSyncStatusAsync_marks_a_failed_probe_as_error_without_affecting_the_others()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var repositoryIds = await SeedExtraRepositoriesAsync(ctx);
        var failing = repositoryIds[0];
        var failingName = $"extra-repo-{failing}";

        ctx.WorkerBridge.Respond("GetRepositoryVersion", args =>
        {
            var repositoryName = (string)args.GetType().GetProperty("repositoryName")!.GetValue(args)!;
            return repositoryName == failingName
                ? new WorkerCommandResponse(false, null, "boom")
                : new WorkerCommandResponse(true, new { exists = true, version = (string?)null, branch = "feature/x", versionProbed = false }, null);
        });

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var statuses = await git.GetRepoSyncStatusAsync(ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync());

        Assert.Equal(RepoSyncStatus.Error, statuses[failing]);
        Assert.Equal(RepoSyncStatus.InSync, statuses[ctx.RepositoryId]);
        Assert.All(repositoryIds.Skip(1), id => Assert.Equal(RepoSyncStatus.InSync, statuses[id]));
    }

    [Fact]
    public async Task Default_tip_probes_for_merged_feature_repositories_run_in_parallel_bounded_by_MaxParallelOperations()
    {
        const int maxParallel = 2;
        await using var ctx = await SyncStateTestContext.CreateAsync(
            configureServices: services => services.Configure<WorkspaceOptions>(o => o.MaxParallelOperations = maxParallel));
        await ctx.UseDotNetDependencyProfileAsync();
        var extraIds = await SeedExtraRepositoriesAsync(ctx);
        var featureContextId = await SeedFeatureWithMergedPullRequestsAsync(ctx);

        var probe = new ConcurrencyProbe();
        ctx.WorkerBridge.Respond("SyncRepository", new
        {
            success = true,
            version = "2.0.0",
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
        });
        ctx.WorkerBridge.RespondAsync("GetGitVersionAtDefaultTip", async (_, ct) =>
        {
            await probe.RunAsync(ct);
            return new WorkerCommandResponse(true, new { success = true, version = "9.9.9", defaultBranch = "main" }, null);
        });

        await using (var scope = ctx.CreateScope())
        {
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            await git.SyncAsync(ctx.WorkspaceId, featureContextId);
        }

        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var versions = await db.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == featureContextId.Value)
            .Select(s => s.GitVersion)
            .ToListAsync();

        // Every merged repository took its version from the default tip, and the probes overlapped within the limit.
        Assert.Equal(extraIds.Count + 1, versions.Count);
        Assert.All(versions, v => Assert.Equal("9.9.9", v));
        probe.AssertBoundedBy(maxParallel);
    }

    /// <summary>Adds repositories linked to the seeded workspace, named <c>extra-repo-{id}</c>, and returns their ids.</summary>
    private static async Task<List<int>> SeedExtraRepositoriesAsync(SyncStateTestContext ctx)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var connectorId = await db.Connectors.Select(c => c.ConnectorId).FirstAsync();

        var ids = new List<int>();
        for (var i = 0; i < ExtraRepositories; i++)
        {
            var repository = new Repository
            {
                ConnectorId = connectorId,
                RepositoryName = $"placeholder-{i}",
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = $"https://github.com/acme/placeholder-{i}.git",
            };
            db.Repositories.Add(repository);
            await db.SaveChangesAsync();

            repository.RepositoryName = $"extra-repo-{repository.RepositoryId}";
            repository.CloneUrl = $"https://github.com/acme/{repository.RepositoryName}.git";
            db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = repository.RepositoryId,
                GitVersion = "1.0.0",
                BranchName = "feature/x",
                DefaultBranchName = "main",
                BranchHasUpstream = true,
                SyncStatus = RepoSyncStatus.InSync,
                RepositoryType = ProjectType.Library,
            });
            await db.SaveChangesAsync();
            ids.Add(repository.RepositoryId);
        }

        return ids;
    }

    /// <summary>A Feature whose every repository (seeded and extra) has a merged pull request.</summary>
    private static async Task<WorkspaceFeatureContextId> SeedFeatureWithMergedPullRequestsAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Rate-limiting the connector makes the Sync's PR reconcile report failure, which leaves the seeded
        // merged rows untouched (same trick as WorkspaceProfileVersionStateTests).
        scope.ServiceProvider
            .GetRequiredService<IGitHubRateLimitTracker>()
            .PauseUntil("github-prod", DateTimeOffset.UtcNow.AddMinutes(5));

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feat-parallel",
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

        var links = await db.WorkspaceRepositories
            .Where(l => l.WorkspaceId == ctx.WorkspaceId)
            .ToListAsync();
        foreach (var link in links)
        {
            db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                WorktreePath = $@"C:\Users\test\.graymoon\test-ws\features\feat-parallel\repo-{link.WorkspaceRepositoryId}",
                State = WorkspaceFeatureRepositoryState.Ready,
                BaseCommitSha = "abc123",
                CreatedAt = DateTime.UtcNow,
            });
            db.WorkspaceRepositoryContextPullRequests.Add(new WorkspaceRepositoryContextPullRequest
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                PullRequestNumber = 7,
                State = "closed",
                MergedAt = DateTimeOffset.UtcNow,
                LastCheckedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}

/// <summary>Records how many callers are inside <see cref="RunAsync"/> at once.</summary>
internal sealed class ConcurrencyProbe
{
    private int _inFlight;
    private int _peak;

    public int Peak => Volatile.Read(ref _peak);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
        {
        }

        try
        {
            await Task.Delay(75, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>The limit is never exceeded, and (when it allows overlap) it is actually reached.</summary>
    public void AssertBoundedBy(int limit)
    {
        Assert.True(Peak <= limit, $"Peak concurrency {Peak} exceeded the limit {limit}.");
        Assert.True(Peak == limit, $"Peak concurrency {Peak} never reached the limit {limit}; the calls are not running in parallel.");
    }
}
