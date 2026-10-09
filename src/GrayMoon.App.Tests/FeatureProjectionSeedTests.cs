using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Features;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Feature creation finalization on a real SQLite model: the seed is idempotent and structure-only, never writes the
/// Feature's GitVersion, and version-derived dependency status stays pending until the deferred sync arrives.
/// </summary>
public sealed class FeatureProjectionSeedTests
{
    private const string FeatureName = "BAM-1-test";

    private sealed record Fixture(
        SyncStateTestContext Ctx,
        WorkspaceFeatureContextId Special,
        WorkspaceFeatureContextId Feature,
        int RepoA,
        int RepoB,
        int LinkA,
        int LinkB,
        string PathA,
        string PathB);

    private static async Task<Fixture> CreateAsync(Action<AppDbContext, int, int>? customizeSpecial = null)
    {
        var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.UseDotNetDependencyProfileAsync();

        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();
        var special = await resolver.GetOrCreateSpecialWorkspaceContextIdAsync(ctx.WorkspaceId);

        var repoA = ctx.RepositoryId;
        var connectorId = (await db.Repositories.FirstAsync(r => r.RepositoryId == repoA)).ConnectorId;
        var repositoryB = new Repository
        {
            ConnectorId = connectorId,
            RepositoryName = "graymoon-lib",
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = "https://github.com/acme/graymoon-lib.git",
        };
        db.Repositories.Add(repositoryB);
        await db.SaveChangesAsync();
        var linkB = new WorkspaceRepositoryLink
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = repositoryB.RepositoryId,
            GitVersion = "1.0.0",
            BranchName = "main",
            DefaultBranchName = "main",
            SyncStatus = RepoSyncStatus.InSync,
        };
        db.WorkspaceRepositories.Add(linkB);
        await db.SaveChangesAsync();

        // Workspace graph: App (repo A) references Lib (repo B) at 1.0.0, which is Lib's Workspace version.
        var app = new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId, WorkspaceFeatureContextId = special.Value, RepositoryId = repoA,
            ProjectName = "App", ProjectFilePath = "App/App.csproj", ProjectType = ProjectType.Library, TargetFramework = "net10.0",
        };
        var lib = new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId, WorkspaceFeatureContextId = special.Value, RepositoryId = repositoryB.RepositoryId,
            ProjectName = "Lib", PackageId = "Lib", ProjectFilePath = "Lib/Lib.csproj", ProjectType = ProjectType.Library, TargetFramework = "net10.0",
        };
        db.WorkspaceProjects.AddRange(app, lib);
        await db.SaveChangesAsync();
        db.ProjectDependencies.Add(new ProjectDependency
        {
            DependentProjectId = app.ProjectId, ReferencedProjectId = lib.ProjectId, Version = "1.0.0",
        });
        await db.SaveChangesAsync();
        customizeSpecial?.Invoke(db, ctx.WorkspaceId, special.Value);
        await db.SaveChangesAsync();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = FeatureName,
            LifecycleState = WorkspaceFeatureLifecycleState.Creating,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();
        var featureContext = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatureContexts.Add(featureContext);
        await db.SaveChangesAsync();

        var pathA = @"C:\feat\graymoon-api";
        var pathB = @"C:\feat\graymoon-lib";
        foreach (var (linkId, path) in new[] { (ctx.WorkspaceRepositoryId, pathA), (linkB.WorkspaceRepositoryId, pathB) })
        {
            db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = featureContext.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = linkId,
                WorktreePath = path,
                BaseCommitSha = "abc123",
                CreatedAt = DateTime.UtcNow,
                State = WorkspaceFeatureRepositoryState.Ready,
            });
        }

        await db.SaveChangesAsync();

        return new Fixture(
            ctx, special, new WorkspaceFeatureContextId(featureContext.WorkspaceFeatureContextId),
            repoA, repositoryB.RepositoryId, ctx.WorkspaceRepositoryId, linkB.WorkspaceRepositoryId, pathA, pathB);
    }

    private static async Task SeedAsync(Fixture f)
    {
        await using var scope = f.Ctx.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var ops = (WorkspaceFeatureOperations)scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        await ops.SeedInitialFeatureProjectionsAsync(db, f.Ctx.WorkspaceId, f.Feature, FeatureName, CancellationToken.None);
    }

    private static async Task<AppDbContext> OpenAsync(Fixture f) =>
        await f.Ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();

    private static RepositorySyncNotification FreshSync(Fixture f, int repositoryId, string path, string version) => new()
    {
        WorkspaceId = f.Ctx.WorkspaceId,
        RepositoryId = repositoryId,
        RepositoryPath = path,
        WorkspaceFeatureContextId = f.Feature.Value,
        Version = version,
        Branch = FeatureName,
        FreshWorktree = true,
    };

    [Fact]
    public async Task Seed_copies_structure_and_leaves_feature_versions_pending()
    {
        await using var f = new FixtureHolder(await CreateAsync());
        await SeedAsync(f.Value);

        await using var db = await OpenAsync(f.Value);
        var projects = await db.WorkspaceProjects.AsNoTracking()
            .Where(p => p.WorkspaceFeatureContextId == f.Value.Feature.Value).ToListAsync();
        Assert.Equal(["App", "Lib"], projects.Select(p => p.ProjectName).Order().ToArray());
        Assert.Equal(1, await db.ProjectDependencies.CountAsync(d =>
            projects.Select(p => p.ProjectId).Contains(d.DependentProjectId)));

        var states = await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == f.Value.Feature.Value).ToListAsync();
        Assert.All(states, s =>
        {
            Assert.Null(s.GitVersion);
            Assert.True(s.GitVersionPending);
        });

        // The comparison for App depends on Lib's not-yet-computed version: pending, never green.
        var appState = states.Single(s => s.WorkspaceRepositoryId == f.Value.LinkA);
        Assert.Null(appState.UnmatchedDeps);
        Assert.Equal(1, appState.Dependencies);
    }

    [Fact]
    public async Task Seed_run_twice_is_idempotent()
    {
        await using var f = new FixtureHolder(await CreateAsync());
        await SeedAsync(f.Value);
        await SeedAsync(f.Value);

        await using var db = await OpenAsync(f.Value);
        Assert.Equal(2, await db.WorkspaceProjects.CountAsync(p => p.WorkspaceFeatureContextId == f.Value.Feature.Value));
        var featureIds = await db.WorkspaceProjects.Where(p => p.WorkspaceFeatureContextId == f.Value.Feature.Value)
            .Select(p => p.ProjectId).ToListAsync();
        Assert.Equal(1, await db.ProjectDependencies.CountAsync(d => featureIds.Contains(d.DependentProjectId)));
        Assert.Equal(2, await db.WorkspaceRepositoryContextStates.CountAsync(s => s.WorkspaceFeatureContextId == f.Value.Feature.Value));
    }

    [Fact]
    public async Task Seed_reconciles_rows_an_early_sync_already_wrote_and_keeps_their_version()
    {
        await using var f = new FixtureHolder(await CreateAsync());
        await using (var db = await OpenAsync(f.Value))
        {
            // As if a checkout sync had run before the seed: project row, state row with the Feature's own version.
            db.WorkspaceProjects.Add(new WorkspaceProject
            {
                WorkspaceId = f.Value.Ctx.WorkspaceId, WorkspaceFeatureContextId = f.Value.Feature.Value,
                RepositoryId = f.Value.RepoA, ProjectName = "App", ProjectFilePath = "stale.csproj",
            });
            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = f.Value.Feature.Value, WorkspaceRepositoryId = f.Value.LinkB,
                GitVersion = "2.0.0", GitVersionPending = false, SyncStatus = RepoSyncStatus.NeedsSync,
            });
            await db.SaveChangesAsync();
        }

        await SeedAsync(f.Value);

        await using var verify = await OpenAsync(f.Value);
        var apps = await verify.WorkspaceProjects.AsNoTracking()
            .Where(p => p.WorkspaceFeatureContextId == f.Value.Feature.Value && p.ProjectName == "App").ToListAsync();
        Assert.Single(apps);
        Assert.Equal("App/App.csproj", apps[0].ProjectFilePath);

        var libState = await verify.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == f.Value.Feature.Value && s.WorkspaceRepositoryId == f.Value.LinkB);
        Assert.Equal("2.0.0", libState.GitVersion);
        Assert.False(libState.GitVersionPending);
        Assert.Equal(RepoSyncStatus.NeedsSync, libState.SyncStatus);

        // Lib is resolved at 2.0.0 while App still references 1.0.0: a real, validated mismatch.
        var appState = await verify.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == f.Value.Feature.Value && s.WorkspaceRepositoryId == f.Value.LinkA);
        Assert.Equal(1, appState.UnmatchedDeps);
    }

    [Fact]
    public async Task Seed_maps_source_projects_with_one_identity_onto_a_single_feature_project()
    {
        await using var f = new FixtureHolder(await CreateAsync((db, workspaceId, specialId) =>
            db.WorkspaceProjects.Add(new WorkspaceProject
            {
                WorkspaceId = workspaceId, WorkspaceFeatureContextId = specialId, RepositoryId = f0RepoA(db),
                ProjectName = "app", ProjectFilePath = "other/app.csproj",
            })));

        await SeedAsync(f.Value);

        await using var db = await OpenAsync(f.Value);
        var names = await db.WorkspaceProjects.AsNoTracking()
            .Where(p => p.WorkspaceFeatureContextId == f.Value.Feature.Value && p.RepositoryId == f.Value.RepoA)
            .Select(p => p.ProjectName).ToListAsync();
        Assert.Single(names);
    }

    private static int f0RepoA(AppDbContext db) => db.Repositories.OrderBy(r => r.RepositoryId).First().RepositoryId;

    [Fact]
    public async Task Deferred_versions_after_seed_produce_the_required_updates_without_a_manual_sync()
    {
        await using var f = new FixtureHolder(await CreateAsync());
        await SeedAsync(f.Value);

        await using var scope = f.Value.Ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        // Lib's Feature version differs from the Workspace's: App must be reported as needing an update.
        await handler.HandleAsync(FreshSync(f.Value, f.Value.RepoB, f.Value.PathB, "1.0.1"));
        await handler.HandleAsync(FreshSync(f.Value, f.Value.RepoA, f.Value.PathA, "1.0.0"));

        await using var db = await OpenAsync(f.Value);
        var appState = await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == f.Value.Feature.Value && s.WorkspaceRepositoryId == f.Value.LinkA);
        Assert.Equal(1, appState.UnmatchedDeps);
        Assert.False(appState.GitVersionPending);
    }

    [Fact]
    public async Task Deferred_versions_equal_to_the_references_stay_green()
    {
        await using var f = new FixtureHolder(await CreateAsync());
        await SeedAsync(f.Value);

        await using var scope = f.Value.Ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();
        await handler.HandleAsync(FreshSync(f.Value, f.Value.RepoB, f.Value.PathB, "1.0.0"));
        await handler.HandleAsync(FreshSync(f.Value, f.Value.RepoA, f.Value.PathA, "1.0.0"));

        await using var db = await OpenAsync(f.Value);
        var appState = await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == f.Value.Feature.Value && s.WorkspaceRepositoryId == f.Value.LinkA);
        Assert.Equal(0, appState.UnmatchedDeps);
    }

    [Fact]
    public async Task Sync_arriving_during_finalization_waits_for_the_barrier_and_then_applies()
    {
        await using var f = new FixtureHolder(await CreateAsync());
        var coordinator = f.Value.Ctx.Resolve<FeatureFinalizationCoordinator>();
        var barrier = coordinator.Arm(f.Value.Feature.Value);

        await using var scope = f.Value.Ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();
        var pending = handler.HandleAsync(FreshSync(f.Value, f.Value.RepoB, f.Value.PathB, "3.0.0"));

        await Task.Delay(200);
        Assert.False(pending.IsCompleted);
        await using (var db = await OpenAsync(f.Value))
            Assert.False(await db.WorkspaceRepositoryContextStates.AnyAsync(s =>
                s.WorkspaceFeatureContextId == f.Value.Feature.Value && s.GitVersion == "3.0.0"));

        await SeedAsync(f.Value);
        barrier.Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(30));

        await using var verify = await OpenAsync(f.Value);
        var libState = await verify.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == f.Value.Feature.Value && s.WorkspaceRepositoryId == f.Value.LinkB);
        Assert.Equal("3.0.0", libState.GitVersion);
    }

    [Fact]
    public async Task Barrier_is_per_context_nested_and_released_by_dispose()
    {
        var coordinator = new FeatureFinalizationCoordinator();
        await coordinator.WaitForFinalizationAsync(1);

        var outer = coordinator.Arm(1);
        var inner = coordinator.Arm(1);
        Assert.True(coordinator.IsFinalizing(1));
        Assert.False(coordinator.IsFinalizing(2));

        var wait = coordinator.WaitForFinalizationAsync(1);
        inner.Dispose();
        inner.Dispose();
        Assert.False(wait.IsCompleted);
        outer.Dispose();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(coordinator.IsFinalizing(1));
    }

    [Fact]
    public async Task Projection_gate_serializes_writers_of_one_context_only()
    {
        var coordinator = new FeatureFinalizationCoordinator();
        using var first = await coordinator.AcquireProjectionAsync(1);
        var second = coordinator.AcquireProjectionAsync(1);
        using var otherContext = await coordinator.AcquireProjectionAsync(2);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Save_failures_surface_the_inner_cause()
    {
        var inner = new InvalidOperationException("UNIQUE constraint failed: WorkspaceProjects.ProjectName");
        var ex = new DbUpdateException("An error occurred while saving the entity changes. See the inner exception for details.", inner);

        var text = WorkspaceFeatureOperations.DescribeFailureMessage(ex);

        Assert.Contains("An error occurred while saving", text);
        Assert.Contains("UNIQUE constraint failed", text);
        Assert.Equal("plain", WorkspaceFeatureOperations.DescribeFailureMessage(new InvalidOperationException("plain")));
    }

    private sealed class FixtureHolder(Fixture value) : IAsyncDisposable
    {
        public Fixture Value { get; } = value;
        public ValueTask DisposeAsync() => Value.Ctx.DisposeAsync();
    }
}
