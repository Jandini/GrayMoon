using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Three rows are created the first time someone needs them: the special Workspace context of a Workspace, the
/// selected-context row of a Workspace, and a setting. Each writer reads, finds none and adds. Two callers that
/// arrive together (the page and the sidebar of a new Workspace, two tabs, a hook sync) both find none, and the
/// later save fails the unique index. That is not an error of the caller: the row it wanted is there. Every test
/// lets a second context store the row at the moment the writer under test starts its save, which is the order
/// of a lost race, and expects what an uncontended call would have left behind.
/// </summary>
public sealed class OneTimeRowCreationRaceTests
{
    private const string Key = AppSettingRepository.SidebarCollapsedKey;

    [Fact]
    public async Task Special_context_another_caller_created_first_is_returned_to_the_caller_that_lost()
    {
        var workspaceId = 0;
        var race = new RunBeforeFirstSave(
            db => db.ChangeTracker.Entries<WorkspaceFeatureContext>().Any(e => e.State == EntityState.Added && e.Entity.WorkspaceId == workspaceId),
            async other =>
            {
                // The other caller ran the whole method: the context and the selected-context row.
                var context = new WorkspaceFeatureContext { WorkspaceId = workspaceId, Kind = WorkspaceFeatureContextKind.Workspace, CreatedAt = DateTime.UtcNow };
                other.WorkspaceFeatureContexts.Add(context);
                await other.SaveChangesAsync();
                other.WorkspaceSelectedFeatureContexts.Add(new WorkspaceSelectedFeatureContext
                {
                    WorkspaceId = workspaceId,
                    WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                    UpdatedAt = DateTime.UtcNow,
                });
                await other.SaveChangesAsync();
            });
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        workspaceId = await AddWorkspaceWithoutSpecialContextAsync(factory);
        race.Bind(factory);

        WorkspaceFeatureContextId result;
        await using (var scope = ctx.CreateScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>()
                .GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId);
        }

        Assert.Equal(1, race.Fired);
        await using var read = await factory.CreateDbContextAsync();
        var stored = await read.WorkspaceFeatureContexts.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).ToListAsync();
        var only = Assert.Single(stored);
        Assert.Equal(only.WorkspaceFeatureContextId, result.Value);
        var selected = Assert.Single(await read.WorkspaceSelectedFeatureContexts.AsNoTracking().Where(s => s.WorkspaceId == workspaceId).ToListAsync());
        Assert.Equal(only.WorkspaceFeatureContextId, selected.WorkspaceFeatureContextId);
    }

    [Fact]
    public async Task Special_context_is_returned_when_another_caller_stored_the_selected_row_first()
    {
        // The second save of the method: this caller created the context, and before it adds the selected-context
        // row a page of the same Workspace selects that context (SetSelectedAsync).
        var workspaceId = 0;
        var race = new RunBeforeFirstSave(
            db => db.ChangeTracker.Entries<WorkspaceSelectedFeatureContext>().Any(e => e.State == EntityState.Added && e.Entity.WorkspaceId == workspaceId),
            async other =>
            {
                var contextId = await other.WorkspaceFeatureContexts.Where(c => c.WorkspaceId == workspaceId).Select(c => c.WorkspaceFeatureContextId).SingleAsync();
                other.WorkspaceSelectedFeatureContexts.Add(new WorkspaceSelectedFeatureContext { WorkspaceId = workspaceId, WorkspaceFeatureContextId = contextId, UpdatedAt = DateTime.UtcNow });
                await other.SaveChangesAsync();
            });
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        workspaceId = await AddWorkspaceWithoutSpecialContextAsync(factory);
        race.Bind(factory);

        WorkspaceFeatureContextId result;
        await using (var scope = ctx.CreateScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>()
                .GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId);
        }

        Assert.Equal(1, race.Fired);
        await using var read = await factory.CreateDbContextAsync();
        var only = Assert.Single(await read.WorkspaceFeatureContexts.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).ToListAsync());
        Assert.Equal(only.WorkspaceFeatureContextId, result.Value);
        var selected = Assert.Single(await read.WorkspaceSelectedFeatureContexts.AsNoTracking().Where(s => s.WorkspaceId == workspaceId).ToListAsync());
        Assert.Equal(only.WorkspaceFeatureContextId, selected.WorkspaceFeatureContextId);
    }

    [Fact]
    public async Task Selection_is_stored_when_another_caller_added_the_selected_row_first()
    {
        var workspaceId = 0;
        var specialId = 0;
        var race = new RunBeforeFirstSave(
            db => db.ChangeTracker.Entries<WorkspaceSelectedFeatureContext>().Any(e => e.State == EntityState.Added && e.Entity.WorkspaceId == workspaceId),
            async other =>
            {
                other.WorkspaceSelectedFeatureContexts.Add(new WorkspaceSelectedFeatureContext { WorkspaceId = workspaceId, WorkspaceFeatureContextId = specialId, UpdatedAt = DateTime.UtcNow });
                await other.SaveChangesAsync();
            });
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        workspaceId = ctx.WorkspaceId;
        specialId = (await ctx.GetSpecialContextIdAsync()).Value;
        var featureContextId = await AddFeatureContextAsync(factory, workspaceId);
        await using (var seed = await factory.CreateDbContextAsync())
            await seed.WorkspaceSelectedFeatureContexts.Where(s => s.WorkspaceId == workspaceId).ExecuteDeleteAsync();
        race.Bind(factory);

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkspaceSelectedFeatureContextService>()
                .SetSelectedAsync(workspaceId, new WorkspaceFeatureContextId(featureContextId));
        }

        Assert.Equal(1, race.Fired);
        // This call came last, so its selection is the stored one, as it is for a call nobody raced.
        Assert.Equal(featureContextId, await ReadSelectedAsync(factory, workspaceId));
    }

    [Fact]
    public async Task Selection_is_stored_when_the_selected_row_was_removed_after_it_was_read()
    {
        // The row goes with the context it points to (cascade) when a Feature is removed in another tab.
        var workspaceId = 0;
        var race = new RunBeforeFirstSave(
            db => db.ChangeTracker.Entries<WorkspaceSelectedFeatureContext>().Any(e => e.State == EntityState.Modified && e.Entity.WorkspaceId == workspaceId),
            other => other.WorkspaceSelectedFeatureContexts.Where(s => s.WorkspaceId == workspaceId).ExecuteDeleteAsync());
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        workspaceId = ctx.WorkspaceId;
        var specialId = (await ctx.GetSpecialContextIdAsync()).Value;
        var featureContextId = await AddFeatureContextAsync(factory, workspaceId);
        await using (var seed = await factory.CreateDbContextAsync())
        {
            await seed.WorkspaceSelectedFeatureContexts.Where(s => s.WorkspaceId == workspaceId).ExecuteDeleteAsync();
            seed.WorkspaceSelectedFeatureContexts.Add(new WorkspaceSelectedFeatureContext { WorkspaceId = workspaceId, WorkspaceFeatureContextId = featureContextId, UpdatedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        race.Bind(factory);

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkspaceSelectedFeatureContextService>()
                .SetSelectedAsync(workspaceId, new WorkspaceFeatureContextId(specialId));
        }

        Assert.Equal(1, race.Fired);
        Assert.Equal(specialId, await ReadSelectedAsync(factory, workspaceId));
    }

    [Fact]
    public async Task Setting_holds_this_callers_value_when_another_caller_added_the_key_first()
    {
        var race = new RunBeforeFirstSave(
            db => db.ChangeTracker.Entries<Setting>().Any(e => e.State == EntityState.Added && e.Entity.Key == Key),
            async other =>
            {
                other.Settings.Add(new Setting { Key = Key, Value = "false" });
                await other.SaveChangesAsync();
            });
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        race.Bind(factory);

        await using (var scope = ctx.CreateScope())
        {
            // The repository saves on the scope's context, which other services of the scope use too. What they
            // track must survive the lost race: a loaded row stays tracked and a pending change is still saved.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var link = await db.WorkspaceRepositories.SingleAsync(wr => wr.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
            var workspace = await db.Workspaces.SingleAsync(w => w.WorkspaceId == ctx.WorkspaceId);
            link.OutgoingCommits = 42;

            await scope.ServiceProvider.GetRequiredService<AppSettingRepository>().SetValueAsync(Key, "true");

            Assert.Equal(EntityState.Unchanged, db.Entry(link).State);
            Assert.Equal(EntityState.Unchanged, db.Entry(workspace).State);
            Assert.Single(db.ChangeTracker.Entries<Setting>(), e => e.Entity.Key == Key);
        }

        Assert.Equal(1, race.Fired);
        Assert.Equal(["true"], await ReadSettingValuesAsync(factory));
        Assert.Equal(42, (await ctx.ReadLinkAsync()).OutgoingCommits);
    }

    [Fact]
    public async Task Setting_holds_this_callers_value_when_another_caller_removed_the_key_after_it_was_read()
    {
        var race = new RunBeforeFirstSave(
            db => db.ChangeTracker.Entries<Setting>().Any(e => e.State == EntityState.Modified && e.Entity.Key == Key),
            other => other.Settings.Where(s => s.Key == Key).ExecuteDeleteAsync());
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await SeedSettingAsync(factory, "false");
        race.Bind(factory);

        await using (var scope = ctx.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppSettingRepository>().SetValueAsync(Key, "true");

        Assert.Equal(1, race.Fired);
        Assert.Equal(["true"], await ReadSettingValuesAsync(factory));
    }

    [Fact]
    public async Task Clearing_a_setting_another_caller_cleared_first_leaves_it_cleared()
    {
        var race = new RunBeforeFirstSave(
            db => db.ChangeTracker.Entries<Setting>().Any(e => e.State == EntityState.Deleted && e.Entity.Key == Key),
            other => other.Settings.Where(s => s.Key == Key).ExecuteDeleteAsync());
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await SeedSettingAsync(factory, "false");
        race.Bind(factory);

        await using (var scope = ctx.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppSettingRepository>().SetValueAsync(Key, null);

        Assert.Equal(1, race.Fired);
        Assert.Empty(await ReadSettingValuesAsync(factory));
    }

    [Fact]
    public async Task A_unique_violation_of_another_row_saved_with_the_setting_still_fails()
    {
        // The repository only settles its own row. A conflict on anything else the scope saves is a real one.
        await using var ctx = await SyncStateTestContext.CreateAsync();

        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Settings.Add(new Setting { Key = AppSettingRepository.FeatureStorageRootPathKey, Value = "duplicate of the seeded key" });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            () => scope.ServiceProvider.GetRequiredService<AppSettingRepository>().SetValueAsync(Key, "true"));
        Assert.True(DbWriteConflict.IsUniqueViolation(ex));
    }

    private static async Task<int> AddWorkspaceWithoutSpecialContextAsync(IDbContextFactory<AppDbContext> factory)
    {
        // The seeded Workspace got its special context from the schema migration. A new, imported or restored
        // Workspace has none until the first caller asks for it.
        await using var db = await factory.CreateDbContextAsync();
        var workspace = new Workspace { Name = "second-ws", RootPath = @"C:\gm-test-root-2" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        return workspace.WorkspaceId;
    }

    private static async Task<int> AddFeatureContextAsync(IDbContextFactory<AppDbContext> factory, int workspaceId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var context = new WorkspaceFeatureContext { WorkspaceId = workspaceId, Kind = WorkspaceFeatureContextKind.Feature, CreatedAt = DateTime.UtcNow };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();
        return context.WorkspaceFeatureContextId;
    }

    private static async Task<int> ReadSelectedAsync(IDbContextFactory<AppDbContext> factory, int workspaceId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.WorkspaceSelectedFeatureContexts.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .Select(s => s.WorkspaceFeatureContextId)
            .SingleAsync();
    }

    private static async Task SeedSettingAsync(IDbContextFactory<AppDbContext> factory, string value)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Settings.Add(new Setting { Key = Key, Value = value });
        await db.SaveChangesAsync();
    }

    private static async Task<List<string?>> ReadSettingValuesAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Settings.AsNoTracking().Where(s => s.Key == Key).Select(s => s.Value).ToListAsync();
    }

    /// <summary>
    /// When the first save that matches <paramref name="isTheSaveUnderTest"/> is about to run, lets another context
    /// write and commit first. The writer under test has read and decided by then and holds no write lock yet, so
    /// this is the order in which it loses the race. It runs once; the saves of a retry go through untouched.
    /// </summary>
    private sealed class RunBeforeFirstSave(Func<AppDbContext, bool> isTheSaveUnderTest, Func<AppDbContext, Task> otherWriter) : SaveChangesInterceptor
    {
        private IDbContextFactory<AppDbContext>? _factory;
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        /// <summary>Arms the interceptor. Saves made before this, such as the test's own seeding, pass through.</summary>
        public void Bind(IDbContextFactory<AppDbContext> factory) => _factory = factory;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_factory != null
                && eventData.Context is AppDbContext db
                && Volatile.Read(ref _fired) == 0
                && isTheSaveUnderTest(db)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await using var other = await _factory.CreateDbContextAsync(cancellationToken);
                await otherWriter(other);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
