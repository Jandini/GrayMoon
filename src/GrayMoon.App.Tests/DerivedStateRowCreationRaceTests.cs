using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// The dependency stats and the file-version counters are stored on the per-context state row, and both create
/// that row when the context has none for a repository. So does the state writer, on another scope. Two of them
/// that read "no row" at the same time both add it; the unique index on (context, link) lets one through and the
/// other save failed as a whole. The one that loses now writes its values onto the row that was stored.
/// </summary>
public sealed class DerivedStateRowCreationRaceTests
{
    [Fact]
    public async Task Dependency_stats_are_stored_on_the_row_another_writer_added_first()
    {
        var race = new StoreStateRowFirst();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        await ctx.UseDotNetDependencyProfileAsync();
        var special = await ctx.GetSpecialContextIdAsync();
        await RemoveStateRowsAsync(ctx);
        race.Bind(ctx.Resolve<IDbContextFactory<AppDbContext>>());

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>()
                .RecomputeAndPersistRepositoryDependencyStatsAsync(ctx.WorkspaceId, special.Value);
        }

        Assert.Equal(1, race.Fired);
        var state = await ReadStateAsync(ctx, special);
        // What the other writer stored is kept, and this writer's values are on the same row.
        Assert.Equal(StoreStateRowFirst.Branch, state.BranchName);
        Assert.Equal(0, state.Dependencies);
        Assert.Equal(0, state.UnmatchedDeps);
    }

    [Fact]
    public async Task File_version_counters_are_stored_on_the_row_another_writer_added_first()
    {
        var race = new StoreStateRowFirst();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var special = await ctx.GetSpecialContextIdAsync();
        await RemoveStateRowsAsync(ctx);
        // Counters a check must clear: the Workspace has no configured version file.
        await ctx.MutateLinkAsync(link => link.OutOfDateFileRepos = 4);
        race.Bind(ctx.Resolve<IDbContextFactory<AppDbContext>>(), state => state.OutOfDateFileRepos = 4);

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>()
                .CheckAndPersistFileVersionStatusAsync(ctx.WorkspaceId, special);
        }

        Assert.Equal(1, race.Fired);
        var state = await ReadStateAsync(ctx, special);
        Assert.Equal(StoreStateRowFirst.Branch, state.BranchName);
        Assert.Null(state.OutOfDateFileRepos);
        Assert.Null((await ctx.ReadLinkAsync()).OutOfDateFileRepos);
    }

    [Fact]
    public async Task A_unique_violation_that_is_not_a_state_row_stored_meanwhile_still_fails()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var special = await ctx.GetSpecialContextIdAsync();
        await RemoveStateRowsAsync(ctx);
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Two rows for one link in one save: nobody else stored anything.
        for (var i = 0; i < 2; i++)
        {
            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = special.Value,
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            });
        }

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveAdoptingStoredStateRowsAsync((_, _) => { }));

        Assert.True(DbWriteConflict.IsUniqueViolation(ex));
    }

    private static async Task RemoveStateRowsAsync(SyncStateTestContext ctx)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await db.WorkspaceRepositoryContextStates.ExecuteDeleteAsync();
    }

    private static async Task<WorkspaceRepositoryContextState> ReadStateAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == contextId.Value && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
    }

    /// <summary>
    /// Stands in for the other writer: when a save is about to add state rows, the same rows are stored from
    /// another context first, once.
    /// </summary>
    private sealed class StoreStateRowFirst : SaveChangesInterceptor
    {
        public const string Branch = "stored-by-the-other-writer";

        private IDbContextFactory<AppDbContext>? _factory;
        private Action<WorkspaceRepositoryContextState>? _alsoSet;
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public void Bind(IDbContextFactory<AppDbContext> factory, Action<WorkspaceRepositoryContextState>? alsoSet = null)
        {
            _alsoSet = alsoSet;
            _factory = factory;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_factory is { } factory && eventData.Context is AppDbContext db)
            {
                var added = db.ChangeTracker.Entries<WorkspaceRepositoryContextState>()
                    .Where(entry => entry.State == EntityState.Added)
                    .Select(entry => (entry.Entity.WorkspaceFeatureContextId, entry.Entity.WorkspaceRepositoryId))
                    .ToList();
                if (added.Count > 0 && Interlocked.Exchange(ref _fired, 1) == 0)
                {
                    await using var other = await factory.CreateDbContextAsync(cancellationToken);
                    foreach (var (contextId, linkId) in added)
                    {
                        var row = new WorkspaceRepositoryContextState
                        {
                            WorkspaceFeatureContextId = contextId,
                            WorkspaceRepositoryId = linkId,
                            BranchName = Branch,
                        };
                        _alsoSet?.Invoke(row);
                        other.WorkspaceRepositoryContextStates.Add(row);
                    }

                    await other.SaveChangesAsync(cancellationToken);
                }
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
