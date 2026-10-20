using System.Reflection;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using GrayMoon.Common.Git;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

public class SqlitePerformanceTests
{
    [Fact]
    public async Task Factory_connections_apply_busy_timeout_and_foreign_keys()
    {
        var connectionString = $"Data Source=file:gmpragma{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(SqliteConnectionPragmasInterceptor.Instance)
            .Options;
        var factory = new TestFactory(options);

        await using (var first = await factory.CreateDbContextAsync())
        {
            await first.Database.OpenConnectionAsync();
            Assert.Equal(SqliteConnectionPragmasInterceptor.BusyTimeoutMilliseconds, await ReadPragmaAsync(first, "busy_timeout"));
            Assert.Equal(1, await ReadPragmaAsync(first, "foreign_keys"));
        }

        await using var second = await factory.CreateDbContextAsync();
        await second.Database.OpenConnectionAsync();
        Assert.Equal(SqliteConnectionPragmasInterceptor.BusyTimeoutMilliseconds, await ReadPragmaAsync(second, "busy_timeout"));
        Assert.Equal(1, await ReadPragmaAsync(second, "foreign_keys"));

        await using var raw = keepAlive.CreateCommand();
        raw.CommandText = "PRAGMA busy_timeout;";
        var rawTimeout = Convert.ToInt32(await raw.ExecuteScalarAsync());
        Assert.NotEqual(SqliteConnectionPragmasInterceptor.BusyTimeoutMilliseconds, rawTimeout);
    }

    [Fact]
    public async Task Action_upserts_on_different_repositories_do_not_share_a_context()
    {
        var connectionString = $"Data Source=file:gmactions{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var factory = new TestFactory(options);
        int firstLink;
        int secondLink;
        await using (var db = await factory.CreateDbContextAsync())
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
            await db.SaveChangesAsync();
            var repoA = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "a", OrgName = "acme", Visibility = "Public", CloneUrl = "https://example/a.git" };
            var repoB = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "b", OrgName = "acme", Visibility = "Public", CloneUrl = "https://example/b.git" };
            db.Repositories.AddRange(repoA, repoB);
            var workspace = new Workspace { Name = "ws", RootPath = @"C:\gm" };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();
            var linkA = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repoA.RepositoryId };
            var linkB = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repoB.RepositoryId };
            db.WorkspaceRepositories.AddRange(linkA, linkB);
            await db.SaveChangesAsync();
            firstLink = linkA.WorkspaceRepositoryId;
            secondLink = linkB.WorkspaceRepositoryId;
        }

        var repository = new WorkspaceActionRepository(factory, NullLogger<WorkspaceActionRepository>.Instance);
        var workflows = new List<ActionStatusInfo> { new() { Status = "success" } };
        await Task.WhenAll(
            repository.UpsertAsync(firstLink, workflows, "main"),
            repository.UpsertAsync(secondLink, workflows, "dev"));

        await using var read = await factory.CreateDbContextAsync();
        var rows = await read.WorkspaceRepositoryActions.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.WorkspaceRepositoryId == firstLink && r.BranchName == "main");
        Assert.Contains(rows, r => r.WorkspaceRepositoryId == secondLink && r.BranchName == "dev");
    }

    [Fact]
    public async Task Scanner_persists_enqueue_and_do_not_resolve_the_handler()
    {
        var connectionString = $"Data Source=file:gmscan{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
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
            await db.SaveChangesAsync();
            var repoA = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "a", OrgName = "acme", Visibility = "Public", CloneUrl = "https://example/a.git" };
            var repoB = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "b", OrgName = "acme", Visibility = "Public", CloneUrl = "https://example/b.git" };
            db.Repositories.AddRange(repoA, repoB);
            var workspace = new Workspace { Name = "ws", RootPath = @"C:\gm" };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();
            db.WorkspaceRepositories.AddRange(
                new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repoA.RepositoryId },
                new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repoB.RepositoryId });
            await db.SaveChangesAsync();
        }

        var queue = new RecordingWriteQueue();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkerBridge>(new ConnectedWorker());
        services.AddSingleton<IWorkspaceContextPathResolver>(new FixedPathResolver());
        services.AddSingleton<IGitChangesWorkerClient>(new SuccessStatusClient());
        services.AddSingleton<WorkspaceGitChangesWriteQueue>(queue);
        services.AddScoped<GitChangesSnapshotPushHandler>(_ => throw new InvalidOperationException("request path resolved the snapshot handler"));
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));
        await using var provider = services.BuildServiceProvider();
        var scanner = new GitChangesWorkspaceScanner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GitChangesOptions { MaxParallelRepositoryOperations = 2 }),
            NullLogger<GitChangesWorkspaceScanner>.Instance);

        await using var lookup = new AppDbContext(options);
        var workspaceId = await lookup.Workspaces.Select(w => w.WorkspaceId).SingleAsync();
        var contextId = new WorkspaceFeatureContextId(1);

        await scanner.ScanWorkspaceAsync(workspaceId, contextId, CancellationToken.None, includeLineStats: true);
        Assert.Equal(2, queue.WaitCalls);
        Assert.Equal(0, queue.EnqueueCalls);

        queue.WaitCalls = 0;
        await scanner.ScanWorkspaceAsync(workspaceId, contextId, CancellationToken.None, persistImmediately: true);
        Assert.Equal(2, queue.WaitCalls);

        queue.WaitCalls = 0;
        await scanner.ScanWorkspaceAsync(workspaceId, contextId, CancellationToken.None);
        Assert.Equal(0, queue.WaitCalls);
        Assert.Equal(2, queue.EnqueueCalls);
    }

    [Fact]
    public void Sync_writer_default_is_four_and_explicit_override_is_kept()
    {
        var empty = new ConfigurationBuilder().Build();
        var overridden = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:MaxConcurrency"] = "16" })
            .Build();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        Assert.Equal(4, ReadMaxConcurrency(new SyncBackgroundService(scopeFactory, NullLogger<SyncBackgroundService>.Instance, empty)));
        Assert.Equal(16, ReadMaxConcurrency(new SyncBackgroundService(scopeFactory, NullLogger<SyncBackgroundService>.Instance, overridden)));
    }

    [Fact]
    public async Task Version_branch_pr_and_push_batches_save_once()
    {
        var saves = new SaveCounter();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(saves));
        var contextId = await ctx.GetSpecialContextIdAsync();

        int secondRepositoryId;
        int secondLinkId;
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
            var link = new WorkspaceRepositoryLink
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = repo.RepositoryId,
                BranchName = "feature/y",
                DefaultBranchName = "main",
            };
            db.WorkspaceRepositories.Add(link);
            await db.SaveChangesAsync();
            secondRepositoryId = repo.RepositoryId;
            secondLinkId = link.WorkspaceRepositoryId;
        }

        saves.Reset();
        await using (var scope = ctx.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
            await writer.ApplyBatchAsync(
                contextId,
                ctx.WorkspaceId,
                [
                    (ctx.RepositoryId, new RepositoryStateSnapshot { IdentityProbed = true, BranchName = "main", GitVersionProbed = true, GitVersion = "1.2.3" }),
                    (secondRepositoryId, new RepositoryStateSnapshot { IdentityProbed = true, BranchName = "dev", GitVersionProbed = true, GitVersion = "2.0.0" }),
                ],
                new RepositoryStateWriteOptions { ReconcilePullRequest = false });
        }

        Assert.Equal(1, saves.Saves);
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var versions = await db.WorkspaceRepositoryContextStates.AsNoTracking()
                .Where(s => s.WorkspaceFeatureContextId == contextId.Value)
                .Select(s => s.GitVersion)
                .ToListAsync();
            Assert.Contains("1.2.3", versions);
            Assert.Contains("2.0.0", versions);
        }

        saves.Reset();
        await using (var scope = ctx.CreateScope())
        {
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            await git.EnsureLocalBranchesPersistedAsync(
                [(ctx.WorkspaceRepositoryId, "feature/a"), (secondLinkId, "feature/b")]);
        }

        Assert.Equal(1, saves.Saves);

        saves.Reset();
        await using (var scope = ctx.CreateScope())
        {
            var prs = scope.ServiceProvider.GetRequiredService<WorkspacePullRequestRepository>();
            await prs.UpsertManyAsync(
            [
                (ctx.WorkspaceRepositoryId, new PullRequestInfo { Number = 1, State = "open" }),
                (secondLinkId, new PullRequestInfo { Number = 2, State = "open" }),
            ]);
        }

        Assert.Equal(1, saves.Saves);

        ctx.WorkerBridge.IsWorkerConnected = false;
        ctx.WorkerBridge.Respond("GetCommitCounts", new { outgoingCommits = 7, incomingCommits = 1, hasUpstream = true });
        saves.Reset();
        await using (var scope = ctx.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var push = new WorkspacePushService(
                ctx.WorkerBridge,
                sp.GetRequiredService<WorkspaceService>(),
                sp.GetRequiredService<WorkspaceRepository>(),
                sp.GetRequiredService<WorkspaceDependencyService>(),
                sp.GetRequiredService<WorkspaceProjectRepository>(),
                sp.GetRequiredService<WorkspaceRepositoryStateWriter>(),
                sp.GetRequiredService<WorkspaceStateRecomputeScope>(),
                sp.GetRequiredService<AppDbContext>(),
                sp.GetRequiredService<IOptions<WorkspaceOptions>>(),
                sp.GetRequiredService<IWorkspaceFeatureContextResolver>(),
                sp.GetRequiredService<IWorkspaceContextPathResolver>(),
                sp.GetRequiredService<IWorkspaceCapabilitiesResolver>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<WorkspacePushService>>(),
                ctx.HubContext);
            await push.UpdateCommitCountsAndUpstreamAfterPushAsync(
                ctx.WorkspaceId,
                contextId,
                [
                    new PushRepoPayload(ctx.RepositoryId, "graymoon-api", null, []),
                    new PushRepoPayload(secondRepositoryId, "graymoon-web", null, []),
                ],
                CancellationToken.None);
        }

        Assert.Equal(1, saves.Saves);
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var counts = await db.WorkspaceRepositoryContextStates.AsNoTracking()
                .Where(s => s.WorkspaceFeatureContextId == contextId.Value)
                .Select(s => s.OutgoingCommits)
                .ToListAsync();
            Assert.Equal(2, counts.Count);
            Assert.All(counts, c => Assert.Equal(7, c));
        }
    }

    [Fact]
    public async Task Read_indexes_are_on_the_model_and_in_strict_step_nine()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        AssertNamedIndex<WorkspaceFeatureContext>(db, "IX_WorkspaceFeatureContexts_WorkspaceId", unique: false);
        AssertNamedIndex<WorkspaceFeatureRepository>(db, "IX_WorkspaceFeatureRepositories_WorkspaceRepositoryId", unique: false);
        AssertNamedIndex<WorkspaceFileLineStatus>(db, "IX_WorkspaceFileLineStatuses_Workspace_Context_Repo", unique: false);

        foreach (var name in new[]
        {
            "IX_WorkspaceFeatureContexts_WorkspaceId",
            "IX_WorkspaceFeatureRepositories_WorkspaceRepositoryId",
            "IX_WorkspaceFileLineStatuses_Workspace_Context_Repo",
        })
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP INDEX IF EXISTS \"{name}\";";
            await drop.ExecuteNonQueryAsync();
        }

        await using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version = 8;";
            await version.ExecuteNonQueryAsync();
        }

        await Migrations.RunAllAsync(db, NullLogger.Instance);

        await using var check = connection.CreateCommand();
        check.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'index' AND name IN (
                'IX_WorkspaceFeatureContexts_WorkspaceId',
                'IX_WorkspaceFeatureRepositories_WorkspaceRepositoryId',
                'IX_WorkspaceFileLineStatuses_Workspace_Context_Repo')
            """;
        Assert.Equal(3, Convert.ToInt32(await check.ExecuteScalarAsync()));
    }

    private static void AssertNamedIndex<T>(AppDbContext db, string name, bool unique) where T : class
    {
        var entity = db.Model.FindEntityType(typeof(T));
        Assert.NotNull(entity);
        var index = entity.GetIndexes().Single(i => i.GetDatabaseName() == name);
        Assert.Equal(unique, index.IsUnique);
    }

    private static int ReadMaxConcurrency(SyncBackgroundService service)
    {
        var field = typeof(SyncBackgroundService).GetField("_maxConcurrency", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (int)field.GetValue(service)!;
    }

    private static async Task<int> ReadPragmaAsync(AppDbContext db, string name)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA " + name + ";";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Saves { get; private set; }
        private int _open;

        public void Reset() => Saves = 0;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_open == 0)
                Saves++;
            _open++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            _open = 0;
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }

        public override Task SaveChangesFailedAsync(
            DbContextErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            _open = 0;
            return base.SaveChangesFailedAsync(eventData, cancellationToken);
        }
    }

    private sealed class RecordingWriteQueue : WorkspaceGitChangesWriteQueue
    {
        public int EnqueueCalls;
        public int WaitCalls;

        public RecordingWriteQueue()
            : base(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkspaceGitChangesWriteQueue>.Instance)
        {
        }

        public override bool Enqueue(GitChangesSnapshotNotification notification)
        {
            EnqueueCalls++;
            return true;
        }

        public override Task EnqueueAndWaitAsync(GitChangesSnapshotNotification notification, CancellationToken cancellationToken = default)
        {
            WaitCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ConnectedWorker : IWorkerBridge
    {
        public bool IsWorkerConnected => true;

        public Task<WorkerCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkerCommandResponse(false, null, "unused"));
    }

    private sealed class FixedPathResolver : IWorkspaceContextPathResolver
    {
        public Task<string> GetContextRootAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
            => Task.FromResult(@"C:\gm\ws");

        public Task<string> GetRepositoryPathAsync(WorkspaceFeatureContextId contextId, int workspaceRepositoryId, CancellationToken cancellationToken = default)
            => Task.FromResult(@"C:\gm\ws\repo");

        public Task<WorkerWorkspaceArgs> GetWorkerArgsAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkerWorkspaceArgs(@"C:\gm", "ws", null));
    }

    private sealed class SuccessStatusClient : IGitChangesWorkerClient
    {
        public Task<GitChangesStatusResult> GetStatusAsync(
            string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
            int workspaceId, int repositoryId, CancellationToken cancellationToken, bool includeLineStats = false)
            => Task.FromResult(new GitChangesStatusResult
            {
                Success = true,
                Snapshot = new GitChangeSnapshot
                {
                    Version = 1,
                    BranchName = "main",
                    Changes = [],
                    ScannedAt = DateTimeOffset.UtcNow,
                },
            });

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
}
