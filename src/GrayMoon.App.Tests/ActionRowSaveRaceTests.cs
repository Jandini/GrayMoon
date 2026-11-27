using System.Text.Json;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// Two refreshes of one repository's CI status can store at once: every open tab refreshes after the same hook
/// broadcast, each from its own scope, and the in-flight coalescing of <c>WorkspaceActionService</c> is a check
/// followed by a set, so two callers can both start a fetch. Each then reads the row, decides to add or update
/// it, and saves. The one that saves second must still store its own workflows, not fail.
/// </summary>
public sealed class ActionRowSaveRaceTests
{
    private const string Branch = "feature/x";

    [Fact]
    public async Task Context_upsert_stores_its_workflows_when_another_refresh_added_the_row_first()
    {
        var race = new RaceBeforeSave<WorkspaceRepositoryContextAction>(EntityState.Added);
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        var contextId = (await ctx.GetSpecialContextIdAsync()).Value;
        race.Run(async () =>
        {
            await using var other = await factory.CreateDbContextAsync();
            other.WorkspaceRepositoryContextActions.Add(new WorkspaceRepositoryContextAction
            {
                WorkspaceFeatureContextId = contextId,
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                BranchName = Branch,
                WorkflowsJson = Json("running"),
                LastCheckedAt = DateTime.UtcNow,
            });
            await other.SaveChangesAsync();
        });

        await NewRepository(factory).UpsertContextAsync(contextId, ctx.WorkspaceRepositoryId, Workflows("success"), Branch);

        Assert.Equal(1, race.Fired);
        await using var read = await factory.CreateDbContextAsync();
        var row = Assert.Single(await read.WorkspaceRepositoryContextActions.AsNoTracking().ToListAsync());
        Assert.Equal(Json("success"), row.WorkflowsJson);
    }

    [Fact]
    public async Task Legacy_upsert_stores_its_workflows_when_another_refresh_added_the_row_first()
    {
        var race = new RaceBeforeSave<WorkspaceRepositoryAction>(EntityState.Added);
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        race.Run(async () =>
        {
            await using var other = await factory.CreateDbContextAsync();
            other.WorkspaceRepositoryActions.Add(new WorkspaceRepositoryAction
            {
                WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
                BranchName = Branch,
                WorkflowsJson = Json("running"),
                LastCheckedAt = DateTime.UtcNow,
            });
            await other.SaveChangesAsync();
        });

        await NewRepository(factory).UpsertAsync(ctx.WorkspaceRepositoryId, Workflows("success"), Branch);

        Assert.Equal(1, race.Fired);
        await using var read = await factory.CreateDbContextAsync();
        var row = Assert.Single(await read.WorkspaceRepositoryActions.AsNoTracking().ToListAsync());
        Assert.Equal(Json("success"), row.WorkflowsJson);
    }

    [Fact]
    public async Task Context_upsert_stores_the_row_again_when_it_was_removed_before_the_update_was_saved()
    {
        // The link and the context are still there, so an uncontended call after the removal would add the row.
        var race = new RaceBeforeSave<WorkspaceRepositoryContextAction>(EntityState.Modified);
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        var contextId = (await ctx.GetSpecialContextIdAsync()).Value;
        var repository = NewRepository(factory);
        await repository.UpsertContextAsync(contextId, ctx.WorkspaceRepositoryId, Workflows("running"), Branch);
        race.Run(async () =>
        {
            await using var other = await factory.CreateDbContextAsync();
            Assert.Equal(1, await other.WorkspaceRepositoryContextActions.ExecuteDeleteAsync());
        });

        await repository.UpsertContextAsync(contextId, ctx.WorkspaceRepositoryId, Workflows("success"), Branch);

        Assert.Equal(1, race.Fired);
        await using var read = await factory.CreateDbContextAsync();
        var row = Assert.Single(await read.WorkspaceRepositoryContextActions.AsNoTracking().ToListAsync());
        Assert.Equal(Json("success"), row.WorkflowsJson);
    }

    [Fact]
    public async Task Legacy_upsert_stores_the_row_again_when_it_was_removed_before_the_update_was_saved()
    {
        var race = new RaceBeforeSave<WorkspaceRepositoryAction>(EntityState.Modified);
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        var repository = NewRepository(factory);
        await repository.UpsertAsync(ctx.WorkspaceRepositoryId, Workflows("running"), Branch);
        race.Run(async () =>
        {
            await using var other = await factory.CreateDbContextAsync();
            Assert.Equal(1, await other.WorkspaceRepositoryActions.ExecuteDeleteAsync());
        });

        await repository.UpsertAsync(ctx.WorkspaceRepositoryId, Workflows("success"), Branch);

        Assert.Equal(1, race.Fired);
        await using var read = await factory.CreateDbContextAsync();
        var row = Assert.Single(await read.WorkspaceRepositoryActions.AsNoTracking().ToListAsync());
        Assert.Equal(Json("success"), row.WorkflowsJson);
    }

    [Fact]
    public async Task A_row_for_a_context_that_no_longer_exists_is_not_stored_and_the_failure_is_reported()
    {
        // A removed Feature takes its context and, by cascade, the action rows. A refresh that was still running
        // must not create the row again: the foreign key refuses it, and that is not a lost race to retry.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        const int removedContextId = 987_654;

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() =>
            NewRepository(factory).UpsertContextAsync(removedContextId, ctx.WorkspaceRepositoryId, Workflows("success"), Branch));

        Assert.False(DbWriteConflict.IsLostRace(ex));
        await using var read = await factory.CreateDbContextAsync();
        Assert.Equal(0, await read.WorkspaceRepositoryContextActions.CountAsync());
    }

    private static WorkspaceActionRepository NewRepository(IDbContextFactory<AppDbContext> factory)
        => new(factory, NullLogger<WorkspaceActionRepository>.Instance);

    private static IReadOnlyList<ActionStatusInfo> Workflows(string status) =>
        [new ActionStatusInfo { Status = status, BranchName = Branch, WorkflowId = 7, WorkflowName = "build" }];

    // The repository serializes with default property names, so the stored text can be compared as a whole.
    private static string Json(string status) => JsonSerializer.Serialize(Workflows(status));

    /// <summary>
    /// Runs the other writer once, when a save of the writer under test is about to store a
    /// <typeparamref name="TRow"/> in <paramref name="state"/>: it has read the row and decided, and has not
    /// written yet. That is the order in which the other refresh wins the race.
    /// </summary>
    private sealed class RaceBeforeSave<TRow>(EntityState state) : SaveChangesInterceptor
        where TRow : class
    {
        private Func<Task>? _otherWriter;
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public void Run(Func<Task> otherWriter) => _otherWriter = otherWriter;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            // The other writer saves through the same interceptor; the flag is set before it runs.
            if (_otherWriter != null
                && eventData.Context is { } db
                && db.ChangeTracker.Entries<TRow>().Any(entry => entry.State == state)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await _otherWriter();
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
