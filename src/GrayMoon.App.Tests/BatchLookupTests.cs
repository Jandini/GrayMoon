using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Queries;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>Batch snapshot / path lookups equal the per-item calls, and the scan fan-out honours its limit.</summary>
public sealed class BatchLookupTests
{
    [Fact]
    public async Task Batch_snapshots_equal_single_snapshots_and_unknown_repository_is_absent()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var contextId = await ctx.GetSpecialContextIdAsync();
        int secondRepositoryId;
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var connectorId = await db.Connectors.Select(c => c.ConnectorId).FirstAsync();
            var repo = new Repository
            {
                ConnectorId = connectorId,
                RepositoryName = "graymoon-web",
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = "https://github.com/acme/graymoon-web.git",
            };
            db.Repositories.Add(repo);
            await db.SaveChangesAsync();
            db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = repo.RepositoryId,
                BranchName = "main",
            });
            await db.SaveChangesAsync();
            secondRepositoryId = repo.RepositoryId;
        }

        await using var readScope = ctx.CreateScope();
        var query = readScope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>();
        const int unknownRepositoryId = 987654;
        var ids = new[] { secondRepositoryId, ctx.RepositoryId, unknownRepositoryId, secondRepositoryId };

        var batch = await query.GetSnapshotsAsync(ctx.WorkspaceId, ids, contextId, isSpecialWorkspace: true);

        Assert.Equal(2, batch.Count);
        Assert.False(batch.ContainsKey(unknownRepositoryId));
        Assert.Null(await query.GetSnapshotAsync(ctx.WorkspaceId, unknownRepositoryId, contextId, true));
        foreach (var id in new[] { ctx.RepositoryId, secondRepositoryId })
        {
            var single = await query.GetSnapshotAsync(ctx.WorkspaceId, id, contextId, true);
            Assert.Equal(single, batch[id]);
        }

        var empty = await query.GetSnapshotsAsync(ctx.WorkspaceId, [], contextId, true);
        Assert.Empty(empty);
    }

    [Fact]
    public async Task Batch_paths_equal_single_paths_for_special_and_feature_contexts()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var special = await ctx.GetSpecialContextIdAsync();
        var rootLinkId = await WorkspaceRoleTestHelpers.AddWorkspaceRoleLinkAsync(scope, ctx.WorkspaceId, "batch-ws-root");
        var feature = await WorkspaceRoleTestHelpers.CreateFeatureContextAsync(db, ctx.WorkspaceId, "batch-feat");
        await WorkspaceRoleTestHelpers.AddFeatureRowAsync(db, feature, ctx.WorkspaceRepositoryId, @"C:/wt/batch-feat/graymoon-api");

        int[] ids = [rootLinkId, ctx.WorkspaceRepositoryId];
        foreach (var contextId in new[] { special, feature })
        {
            var batch = await resolver.GetRepositoryPathsAsync(contextId, ids);
            Assert.Equal(ids.Length, batch.Count);
            foreach (var id in ids)
                Assert.Equal(await resolver.GetRepositoryPathAsync(contextId, id), batch[id]);
        }

        var featurePaths = await resolver.GetRepositoryPathsAsync(feature, ids);
        Assert.Equal(@"C:\wt\batch-feat\graymoon-api", featurePaths[ctx.WorkspaceRepositoryId]);

        Assert.Empty(await resolver.GetRepositoryPathsAsync(special, []));
    }

    [Fact]
    public async Task Batch_paths_throw_like_the_single_call_for_an_unknown_link()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var special = await ctx.GetSpecialContextIdAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.GetRepositoryPathAsync(special, 987654));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.GetRepositoryPathsAsync(special, [ctx.WorkspaceRepositoryId, 987654]));
    }

    [Fact]
    public async Task Scanner_batch_scan_resolves_paths_once_and_reaches_but_never_exceeds_the_limit()
    {
        const int limit = 3;
        var connectionString = $"Data Source=file:gmbatch{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var repositoryIds = new List<int>();
        int workspaceId;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var connector = new Connector
            {
                ConnectorName = "github",
                ConnectorType = ConnectorType.GitHub,
                ApiBaseUrl = "https://api.github.com",
                IsActive = true,
            };
            db.Connectors.Add(connector);
            var workspace = new Workspace { Name = "ws", RootPath = @"C:\gm" };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();
            for (var i = 0; i < 8; i++)
            {
                var repo = new Repository
                {
                    ConnectorId = connector.ConnectorId,
                    RepositoryName = $"r{i}",
                    OrgName = "acme",
                    Visibility = "Public",
                    CloneUrl = $"https://example/r{i}.git",
                };
                db.Repositories.Add(repo);
                await db.SaveChangesAsync();
                db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repo.RepositoryId });
                await db.SaveChangesAsync();
                repositoryIds.Add(repo.RepositoryId);
            }

            workspaceId = workspace.WorkspaceId;
        }

        var resolverProbe = new BatchLookupPathProbe();
        var clientProbe = new BatchLookupStatusProbe(TimeSpan.FromMilliseconds(40));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkerBridge>(new BatchLookupConnectedWorker());
        services.AddSingleton<IWorkspaceContextPathResolver>(resolverProbe);
        services.AddSingleton<IGitChangesWorkerClient>(clientProbe);
        services.AddSingleton<WorkspaceGitChangesWriteQueue>(new BatchLookupWriteQueue());
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));
        await using var provider = services.BuildServiceProvider();
        var scanner = new GitChangesWorkspaceScanner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GitChangesOptions { MaxParallelRepositoryOperations = limit }),
            NullLogger<GitChangesWorkspaceScanner>.Instance);

        var requested = repositoryIds.Take(6).ToList();
        await scanner.ScanRepositoriesAsync(workspaceId, new WorkspaceFeatureContextId(1), requested, CancellationToken.None);

        Assert.Equal(requested.OrderBy(i => i), clientProbe.ScannedRepositoryIds.OrderBy(i => i));
        Assert.Equal(limit, clientProbe.MaxConcurrent);
        Assert.Equal(1, resolverProbe.BatchCalls);
        Assert.Equal(0, resolverProbe.SingleCalls);
    }

    private sealed class BatchLookupPathProbe : IWorkspaceContextPathResolver
    {
        public int BatchCalls;
        public int SingleCalls;

        public Task<string> GetContextRootAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
            => Task.FromResult(@"C:\gm\ws");

        public Task<string> GetRepositoryPathAsync(WorkspaceFeatureContextId contextId, int workspaceRepositoryId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref SingleCalls);
            return Task.FromResult($@"C:\gm\ws\repo{workspaceRepositoryId}");
        }

        public Task<IReadOnlyDictionary<int, string>> GetRepositoryPathsAsync(
            WorkspaceFeatureContextId contextId,
            IReadOnlyCollection<int> workspaceRepositoryIds,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref BatchCalls);
            IReadOnlyDictionary<int, string> map = workspaceRepositoryIds.ToDictionary(id => id, id => $@"C:\gm\ws\repo{id}");
            return Task.FromResult(map);
        }

        public Task<WorkerWorkspaceArgs> GetWorkerArgsAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkerWorkspaceArgs(@"C:\gm", "ws", null));
    }

    private sealed class BatchLookupStatusProbe(TimeSpan hold) : IGitChangesWorkerClient
    {
        private readonly object _gate = new();
        private int _running;

        public int MaxConcurrent { get; private set; }

        public List<int> ScannedRepositoryIds { get; } = [];

        public async Task<GitChangesStatusResult> GetStatusAsync(
            string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
            int workspaceId, int repositoryId, CancellationToken cancellationToken, bool includeLineStats = false)
        {
            lock (_gate)
            {
                _running++;
                MaxConcurrent = Math.Max(MaxConcurrent, _running);
                ScannedRepositoryIds.Add(repositoryId);
            }

            try
            {
                await Task.Delay(hold, cancellationToken);
            }
            finally
            {
                lock (_gate)
                    _running--;
            }

            return new GitChangesStatusResult
            {
                Success = true,
                Snapshot = new GitChangeSnapshot
                {
                    Version = 1,
                    BranchName = "main",
                    Changes = [],
                    ScannedAt = DateTimeOffset.UtcNow,
                },
            };
        }

        public Task<GitChangesDiffResult> GetDiffAsync(string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName, string path, GitDiffComparison comparison, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<GitChangesMutationResult> StageAsync(string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName, GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<GitChangesMutationResult> UnstageAsync(string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName, GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<GitChangesMutationResult> DiscardAsync(string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName, GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<GitChangesCommitResult> CommitAsync(string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName, string commitMessage, bool stageAllFirst, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class BatchLookupConnectedWorker : IWorkerBridge
    {
        public bool IsWorkerConnected => true;

        public Task<WorkerCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkerCommandResponse(false, null, "unused"));
    }

    private sealed class BatchLookupWriteQueue : WorkspaceGitChangesWriteQueue
    {
        public BatchLookupWriteQueue()
            : base(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkspaceGitChangesWriteQueue>.Instance)
        {
        }

        public override bool Enqueue(GitChangesSnapshotNotification notification) => true;

        public override Task EnqueueAndWaitAsync(GitChangesSnapshotNotification notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
