using System.Data.Common;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// <c>WorkspaceProjects</c> and <c>ProjectDependencies</c> rows of a context are replaced by the project merge
/// (a hook sync's reconcile) under the context's projection gate. Two other writers changed the same rows
/// without it, each on its own <see cref="AppDbContext"/>: the generated-package sync of the file-version check,
/// and the package registry mapping. A merge between their read and their save removed or replaced a row they had
/// loaded, and their save failed for an operation that had nothing wrong with it.
/// </summary>
public sealed class ProjectRowWriterGateTests
{
    private const string PackageName = "GrayMoon.Generated.Package";
    private const string ConsumerPath = "src/Consumer/Consumer.csproj";

    [Fact]
    public async Task A_project_merge_cannot_replace_the_edge_between_the_generated_package_syncs_read_and_save()
    {
        // The merge rebuilds every edge of the projects it scanned: the row the sync loaded is deleted and an
        // equal one is inserted. The sync then updated a row that was gone.
        var race = new OtherWriterAtSave(db => db.ChangeTracker.Entries<ProjectDependency>().Any(e => e.State == EntityState.Modified));
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var seed = await SeedAsync(ctx);
        await SyncGeneratedAsync(ctx, seed, seed.SpecialContextId, "1.0.0");
        race.Arm(ctx.Resolve<FeatureFinalizationCoordinator>(), seed.SpecialContextId, () => MergeConsumerAsync(ctx, seed, "2.0.0"));

        await SyncGeneratedAsync(ctx, seed, seed.SpecialContextId, "2.0.0");
        await race.FinishAsync();

        Assert.Equal(1, race.Fired);
        Assert.False(race.RanBetweenReadAndSave);
        var edge = Assert.Single(await ctx.ReadDependenciesAsync());
        Assert.Equal(seed.ConsumerProjectId, edge.DependentProjectId);
        Assert.True(edge.ReferencedProject!.IsGenerated);
        Assert.Equal("2.0.0", edge.Version);
    }

    [Fact]
    public async Task A_project_merge_cannot_add_the_edge_the_generated_package_sync_found_missing()
    {
        // The sync read "no edge" and inserts one; the merge inserted the same edge first and the unique
        // (dependent, referenced) index refused the second.
        var race = new OtherWriterAtSave(db => db.ChangeTracker.Entries<ProjectDependency>().Any(e => e.State == EntityState.Added));
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        var seed = await SeedAsync(ctx);
        await SyncGeneratedAsync(ctx, seed, seed.SpecialContextId, "1.0.0");
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(1, await db.ProjectDependencies.ExecuteDeleteAsync());
        race.Arm(ctx.Resolve<FeatureFinalizationCoordinator>(), seed.SpecialContextId, () => MergeConsumerAsync(ctx, seed, "1.0.0"));

        await SyncGeneratedAsync(ctx, seed, seed.SpecialContextId, "1.0.0");
        await race.FinishAsync();

        Assert.Equal(1, race.Fired);
        Assert.False(race.RanBetweenReadAndSave);
        var edge = Assert.Single(await ctx.ReadDependenciesAsync());
        Assert.Equal(seed.ConsumerProjectId, edge.DependentProjectId);
        Assert.True(edge.ReferencedProject!.IsGenerated);
    }

    [Fact]
    public async Task The_Workspace_cannot_remove_a_generated_package_between_a_Features_read_and_save()
    {
        // The generated rows belong to the special Workspace context and a Feature's sync decides from them, so
        // it holds that context's gate as well as its own. Without it the Workspace's own sync removed the
        // package (and, by cascade, the Feature's edge) while the Feature was about to update that edge.
        var race = new OtherWriterAtSave(db => db.ChangeTracker.Entries<ProjectDependency>().Any(e => e.State == EntityState.Modified));
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        var seed = await SeedAsync(ctx);
        var featureContextId = await AddFeatureConsumerAsync(ctx, seed);
        // The Workspace's sync gives the Feature's consumer its edge too, with the Workspace's version.
        await SyncGeneratedAsync(ctx, seed, seed.SpecialContextId, "1.0.0");
        race.Arm(ctx.Resolve<FeatureFinalizationCoordinator>(), seed.SpecialContextId, async () =>
        {
            await using var scope = ctx.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>()
                .SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, seed.SpecialContextId, []);
        });

        await SyncGeneratedAsync(ctx, seed, featureContextId, "2.0.0");
        await race.FinishAsync();

        Assert.Equal(1, race.Fired);
        Assert.False(race.RanBetweenReadAndSave);
        // The Workspace's sync ran after the Feature's and removed the package with every edge to it.
        await using var read = await factory.CreateDbContextAsync();
        Assert.False(await read.WorkspaceProjects.AnyAsync(p => p.IsGenerated));
        Assert.False(await read.ProjectDependencies.AnyAsync());
    }

    [Fact]
    public async Task A_Features_merge_cannot_remove_the_consumer_the_Workspaces_sync_adds_an_edge_for()
    {
        // The Workspace's sync gives a Feature's consumer its missing edge, so a new Feature has it before its own
        // first check. The consumer row is the Feature's: its merge replaces it under the Feature's gate. Holding
        // only the Workspace's gate, the sync read the consumer, the Feature's merge removed it (the branch no
        // longer has the project), and the sync's insert of an edge to a row that was gone failed.
        var race = new OtherWriterAtSave(db => db.ChangeTracker.Entries<ProjectDependency>().Any(e => e.State == EntityState.Added));
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var seed = await SeedAsync(ctx);
        await SyncGeneratedAsync(ctx, seed, seed.SpecialContextId, "1.0.0");
        // Added after the sync, so its consumer has no edge to the generated package yet.
        var featureContextId = await AddFeatureConsumerAsync(ctx, seed);
        race.Arm(ctx.Resolve<FeatureFinalizationCoordinator>(), featureContextId, () => MergeNoProjectsAsync(ctx, featureContextId));

        await SyncGeneratedAsync(ctx, seed, seed.SpecialContextId, "1.0.0");
        await race.FinishAsync();

        Assert.Equal(1, race.Fired);
        Assert.False(race.RanBetweenReadAndSave);
        // The merge ran after the sync: the Feature's consumer is gone, and its edge with it.
        await using var read = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        Assert.False(await read.WorkspaceProjects.AnyAsync(p => p.WorkspaceFeatureContextId == featureContextId));
        var edge = Assert.Single(await read.ProjectDependencies.AsNoTracking().ToListAsync());
        Assert.Equal(seed.ConsumerProjectId, edge.DependentProjectId);
    }

    [Fact]
    public async Task A_Feature_created_while_the_Workspaces_sync_waited_for_a_gate_is_covered_too()
    {
        // The Features are read before their gates are taken. One that appears while the sync waits for another
        // Feature's gate has rows the sync writes to as well, so its gate must be held at the save.
        var held = new GatesHeldAtEdgeSave();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(held));
        var seed = await SeedAsync(ctx);
        await SyncGeneratedAsync(ctx, seed, seed.SpecialContextId, "1.0.0");
        var coordinator = ctx.Resolve<FeatureFinalizationCoordinator>();
        var first = await AddFeatureConsumerAsync(ctx, seed);

        // The first Feature's gate is busy (its seed, a merge): the sync takes the Workspace's gate and waits.
        var firstGate = await coordinator.AcquireProjectionAsync(first);
        var sync = SyncGeneratedAsync(ctx, seed, seed.SpecialContextId, "1.0.0");
        await Wait.UntilAsync(() => sync.IsCompleted || IsHeld(coordinator, seed.SpecialContextId));
        // Meanwhile a second Feature is created.
        var second = await AddFeatureConsumerAsync(ctx, seed);
        held.Watch(coordinator, [seed.SpecialContextId, first, second]);
        firstGate.Dispose();
        await sync.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEmpty(held.FreeAtSave);
        Assert.All(held.FreeAtSave, free => Assert.Empty(free));
        await using var read = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        Assert.Equal(3, await read.ProjectDependencies.CountAsync(d => d.ReferencedProject!.IsGenerated));
    }

    /// <summary>True when the context's projection gate is taken right now. The probe is withdrawn at once.</summary>
    private static bool IsHeld(FeatureFinalizationCoordinator coordinator, int contextId)
    {
        using var stop = new CancellationTokenSource();
        var probe = coordinator.AcquireProjectionAsync(contextId, stop.Token);
        if (probe.IsCompletedSuccessfully)
        {
            probe.Result.Dispose();
            return false;
        }

        stop.Cancel();
        // Withdrawn; if it was granted in that instant, give it back.
        probe.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
        return true;
    }

    /// <summary>Records, for every save that adds a dependency edge, which of the watched gates were free at that moment.</summary>
    private sealed class GatesHeldAtEdgeSave : SaveChangesInterceptor
    {
        private FeatureFinalizationCoordinator? _coordinator;
        private IReadOnlyList<int> _contextIds = [];

        public List<List<int>> FreeAtSave { get; } = [];

        public void Watch(FeatureFinalizationCoordinator coordinator, IReadOnlyList<int> contextIds)
        {
            _contextIds = contextIds;
            _coordinator = coordinator;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_coordinator is { } coordinator
                && eventData.Context is AppDbContext db
                && db.ChangeTracker.Entries<ProjectDependency>().Any(e => e.State == EntityState.Added))
            {
                FreeAtSave.Add(_contextIds.Where(id => !IsHeld(coordinator, id)).ToList());
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task The_registry_mapping_is_stored_for_the_packages_left_when_a_merge_removed_one_of_them()
    {
        // The registry sync reads the packages, asks the registries (seconds), then stores the answers by
        // project id. A hook sync whose scan no longer has one of the projects removes its row in between.
        var race = new OtherWriterBeforeMapping();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race.BeforeSave, race.BeforeCommand));
        var contextId = (await ctx.GetSpecialContextIdAsync()).Value;
        var connectorId = await AddNuGetConnectorAsync(ctx);
        await MergePackagesAsync(ctx, contextId, "Kept", "Removed");
        var projects = await ctx.ReadProjectsAsync();
        var kept = projects.Single(p => p.ProjectName == "Kept");
        var removed = projects.Single(p => p.ProjectName == "Removed");
        race.Arm(() => MergePackagesAsync(ctx, contextId, "Kept"));

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>().SetPackagesMatchedConnectorsAsync(
                ctx.WorkspaceId,
                new Dictionary<int, int?> { [kept.ProjectId] = connectorId, [removed.ProjectId] = connectorId });
        }

        Assert.Equal(1, race.Fired);
        var stored = Assert.Single(await ctx.ReadProjectsAsync());
        Assert.Equal(kept.ProjectId, stored.ProjectId);
        Assert.Equal(connectorId, stored.MatchedConnectorId);
    }

    [Fact]
    public async Task The_registry_mapping_stores_every_connector_and_a_cleared_match()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var contextId = (await ctx.GetSpecialContextIdAsync()).Value;
        var first = await AddNuGetConnectorAsync(ctx, "nuget-first");
        var second = await AddNuGetConnectorAsync(ctx, "nuget-second");
        await MergePackagesAsync(ctx, contextId, "One", "Two", "Cleared", "Untouched");
        var ids = (await ctx.ReadProjectsAsync()).ToDictionary(p => p.ProjectName, p => p.ProjectId);
        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>().SetPackagesMatchedConnectorsAsync(
                ctx.WorkspaceId,
                new Dictionary<int, int?> { [ids["Cleared"]] = first, [ids["Untouched"]] = second });
        }

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>().SetPackagesMatchedConnectorsAsync(
                ctx.WorkspaceId,
                new Dictionary<int, int?> { [ids["One"]] = first, [ids["Two"]] = second, [ids["Cleared"]] = null });
        }

        var stored = (await ctx.ReadProjectsAsync()).ToDictionary(p => p.ProjectName, p => p.MatchedConnectorId);
        Assert.Equal(first, stored["One"]);
        Assert.Equal(second, stored["Two"]);
        Assert.Null(stored["Cleared"]);
        Assert.Equal(second, stored["Untouched"]);
    }

    [Fact]
    public async Task The_registry_mapping_reaches_a_row_the_same_context_already_tracks()
    {
        // The repository's context lives as long as the circuit and tracks project rows from earlier work. The
        // set-based update goes past the tracker, so the tracked row is given the stored value and stays unmodified.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var contextId = (await ctx.GetSpecialContextIdAsync()).Value;
        var connectorId = await AddNuGetConnectorAsync(ctx);
        await MergePackagesAsync(ctx, contextId, "Kept");

        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tracked = await db.WorkspaceProjects.SingleAsync(p => p.ProjectName == "Kept");
        await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>().SetPackagesMatchedConnectorsAsync(
            ctx.WorkspaceId,
            new Dictionary<int, int?> { [tracked.ProjectId] = connectorId });

        Assert.Equal(connectorId, tracked.MatchedConnectorId);
        Assert.Equal(EntityState.Unchanged, db.Entry(tracked).State);
        Assert.Equal(connectorId, Assert.Single(await ctx.ReadProjectsAsync()).MatchedConnectorId);
    }

    private sealed record Seed(int SpecialContextId, int ProducerRepositoryId, int ConsumerProjectId);

    /// <summary>A producer repository without a project of its own, and the consumer project that references its package.</summary>
    private static async Task<Seed> SeedAsync(SyncStateTestContext ctx)
    {
        var specialContextId = (await ctx.GetSpecialContextIdAsync()).Value;
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        int producerRepositoryId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var consumerRepository = await db.Repositories.AsNoTracking().SingleAsync(r => r.RepositoryId == ctx.RepositoryId);
            var producer = new Repository
            {
                ConnectorId = consumerRepository.ConnectorId,
                RepositoryName = "graymoon-producer",
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = "https://github.com/acme/graymoon-producer.git",
            };
            db.Repositories.Add(producer);
            await db.SaveChangesAsync();
            db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink { WorkspaceId = ctx.WorkspaceId, RepositoryId = producer.RepositoryId });
            await db.SaveChangesAsync();
            producerRepositoryId = producer.RepositoryId;
        }

        var seed = new Seed(specialContextId, producerRepositoryId, 0);
        await MergeConsumerAsync(ctx, seed, "1.0.0");
        return seed with { ConsumerProjectId = (await ctx.ReadProjectsAsync()).Single().ProjectId };
    }

    /// <summary>A Feature context with its own row for the consumer project, as the Feature seed leaves it.</summary>
    private static async Task<int> AddFeatureConsumerAsync(SyncStateTestContext ctx, Seed seed)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var featureContext = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true,
        };
        db.WorkspaceFeatureContexts.Add(featureContext);
        await db.SaveChangesAsync();

        var consumer = new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = featureContext.WorkspaceFeatureContextId,
            RepositoryId = ctx.RepositoryId,
            ProjectName = "Consumer",
            ProjectType = ProjectType.Library,
            ProjectFilePath = ConsumerPath,
            TargetFramework = "net10.0",
        };
        consumer.RefreshIdentityKey();
        db.WorkspaceProjects.Add(consumer);
        await db.SaveChangesAsync();
        return featureContext.WorkspaceFeatureContextId;
    }

    /// <summary>The writer under test: the generated-package sync of one context, on its own scope as a file-version check has it.</summary>
    private static async Task SyncGeneratedAsync(SyncStateTestContext ctx, Seed seed, int contextId, string version)
    {
        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>().SyncGeneratedPackageDependenciesAsync(
            ctx.WorkspaceId,
            contextId,
            [new GeneratedPackageDependencyInfo(ctx.RepositoryId, ConsumerPath, seed.ProducerRepositoryId, PackageName, version)]);
    }

    /// <summary>The other writer: a hook sync's reconcile of the consumer repository in the special Workspace context, on its own scope.</summary>
    private static async Task MergeConsumerAsync(SyncStateTestContext ctx, Seed seed, string referencedVersion)
    {
        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>().ReconcileProjectSnapshotAsync(
            ctx.WorkspaceId,
            seed.SpecialContextId,
            [
                ProjectScanOutcome.Scanned(ctx.RepositoryId,
                [
                    new SyncProjectInfo("Consumer", ProjectType.Library, ConsumerPath, "net10.0", null,
                        [new SyncPackageReference(PackageName, referencedVersion)]),
                ]),
            ],
            mergeEdges: true,
            persistDependencyLevel: false);
    }

    /// <summary>A Feature's scan that found no project in the consumer repository: its merge removes the consumer row, under the Feature's gate only.</summary>
    private static async Task MergeNoProjectsAsync(SyncStateTestContext ctx, int contextId)
    {
        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>().ReconcileProjectSnapshotAsync(
            ctx.WorkspaceId,
            contextId,
            [ProjectScanOutcome.Scanned(ctx.RepositoryId, [])],
            mergeEdges: true,
            persistDependencyLevel: false);
    }

    /// <summary>A scan of the seeded repository that found exactly these package projects.</summary>
    private static async Task MergePackagesAsync(SyncStateTestContext ctx, int contextId, params string[] names)
    {
        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>().MergeWorkspaceProjectsAsync(
            ctx.WorkspaceId,
            ctx.RepositoryId,
            names.Select(name => new SyncProjectInfo(name, ProjectType.Package, $"src/{name}/{name}.csproj", "net10.0", $"Acme.{name}", [])).ToList(),
            contextId);
    }

    private static async Task<int> AddNuGetConnectorAsync(SyncStateTestContext ctx, string name = "nuget-prod")
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var connector = new Connector
        {
            ConnectorName = name,
            ConnectorType = ConnectorType.NuGet,
            ApiBaseUrl = "https://api.nuget.org/v3/index.json",
            IsActive = true,
            IsHealthy = true,
        };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();
        return connector.ConnectorId;
    }

    /// <summary>
    /// When the first save that matches <paramref name="isTheSaveUnderTest"/> is about to run, the writer under
    /// test has read and decided and holds no database lock yet. The other writer takes the projection gate, as
    /// every project merge does. If the gate is free it runs to the end here, between the read and the save, which
    /// is the order that failed. If the writer under test holds the gate, the other writer is started and left
    /// waiting for it, as it would be in the application, and <see cref="FinishAsync"/> lets it complete.
    /// </summary>
    private sealed class OtherWriterAtSave(Func<AppDbContext, bool> isTheSaveUnderTest) : SaveChangesInterceptor
    {
        private FeatureFinalizationCoordinator? _coordinator;
        private Func<Task>? _otherWriter;
        private Task _waiting = Task.CompletedTask;
        private int _gateContextId;
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public bool RanBetweenReadAndSave { get; private set; }

        /// <summary>Arms the interceptor. Saves made before this, such as the test's own seeding, pass through.</summary>
        public void Arm(FeatureFinalizationCoordinator coordinator, int gateContextId, Func<Task> otherWriter)
        {
            _coordinator = coordinator;
            _gateContextId = gateContextId;
            _otherWriter = otherWriter;
        }

        /// <summary>Waits for the other writer when it had to wait for the gate.</summary>
        public Task FinishAsync() => _waiting;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_coordinator != null
                && _otherWriter != null
                && eventData.Context is AppDbContext db
                && Volatile.Read(ref _fired) == 0
                && isTheSaveUnderTest(db)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                using var stopProbing = new CancellationTokenSource();
                var probe = _coordinator.AcquireProjectionAsync(_gateContextId, stopProbing.Token);
                if (probe.IsCompletedSuccessfully)
                {
                    probe.Result.Dispose();
                    await _otherWriter();
                    RanBetweenReadAndSave = true;
                }
                else
                {
                    // The gate is held by the save that is running right now, so it cannot be released before the probe is withdrawn.
                    stopProbing.Cancel();
                    try
                    {
                        (await probe).Dispose();
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    _waiting = _otherWriter();
                }
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Runs the other writer once, just before the registry mapping reaches the database: before the save of a
    /// writer that loaded the rows, or before the UPDATE of one that writes them set-based. Neither holds a
    /// database lock at that point.
    /// </summary>
    private sealed class OtherWriterBeforeMapping
    {
        private Func<Task>? _otherWriter;
        private int _fired;

        public OtherWriterBeforeMapping()
        {
            BeforeSave = new SaveHook(this);
            BeforeCommand = new CommandHook(this);
        }

        public IInterceptor BeforeSave { get; }

        public IInterceptor BeforeCommand { get; }

        public int Fired => Volatile.Read(ref _fired);

        public void Arm(Func<Task> otherWriter) => _otherWriter = otherWriter;

        private async Task FireOnceAsync()
        {
            if (_otherWriter != null && Interlocked.Exchange(ref _fired, 1) == 0)
                await _otherWriter();
        }

        private sealed class SaveHook(OtherWriterBeforeMapping owner) : SaveChangesInterceptor
        {
            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                if (eventData.Context is AppDbContext db
                    && db.ChangeTracker.Entries<WorkspaceProject>().Any(e =>
                        e.State == EntityState.Modified && e.Property(p => p.MatchedConnectorId).IsModified))
                    await owner.FireOnceAsync();

                return await base.SavingChangesAsync(eventData, result, cancellationToken);
            }
        }

        private sealed class CommandHook(OtherWriterBeforeMapping owner) : DbCommandInterceptor
        {
            public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                if (command.CommandText.Contains("UPDATE \"WorkspaceProjects\"", StringComparison.Ordinal)
                    && command.CommandText.Contains("\"MatchedConnectorId\"", StringComparison.Ordinal))
                    await owner.FireOnceAsync();

                return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
            }
        }
    }
}
