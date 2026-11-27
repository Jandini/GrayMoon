using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Nothing serializes the writers of a pull request row: the polling loop of every open tab, the hook sync's
/// pull request reconcile and the forced refresh inside an operation (Return to Default refreshes before its
/// Worker command) all read the row, decide and save on their own context. The one that saves second used to
/// fail, and with it the user action that asked for the refresh. Every Upsert method must instead read again
/// and store what it was asked to store.
/// </summary>
public sealed class PullRequestRowSaveRaceTests
{
    private const int Mine = 7;
    private const int Theirs = 99;

    public static TheoryData<string> ContextWriters => new(ContextWriterByName.Keys);

    public static TheoryData<string> LegacyWriters => new(LegacyWriterByName.Keys);

    private static readonly Dictionary<string, Func<WorkspacePullRequestRepository, int, int, PullRequestInfo?, Task>> ContextWriterByName = new()
    {
        [nameof(WorkspacePullRequestRepository.UpsertContextAsync)] =
            (repository, contextId, linkId, pr) => repository.UpsertContextAsync(contextId, linkId, pr),
        [nameof(WorkspacePullRequestRepository.UpsertContextManyAsync)] =
            (repository, contextId, linkId, pr) => repository.UpsertContextManyAsync(contextId, [(linkId, pr)]),
        [nameof(WorkspacePullRequestRepository.UpsertContextBranchPullRequestsManyAsync)] =
            (repository, contextId, linkId, pr) => repository.UpsertContextBranchPullRequestsManyAsync(contextId, [(linkId, new BranchPullRequests(pr, null))]),
    };

    private static readonly Dictionary<string, Func<WorkspacePullRequestRepository, int, PullRequestInfo?, Task>> LegacyWriterByName = new()
    {
        [nameof(WorkspacePullRequestRepository.UpsertAsync)] =
            (repository, linkId, pr) => repository.UpsertAsync(linkId, pr),
        [nameof(WorkspacePullRequestRepository.UpsertManyAsync)] =
            (repository, linkId, pr) => repository.UpsertManyAsync([(linkId, pr)]),
        [nameof(WorkspacePullRequestRepository.UpsertBranchPullRequestsManyAsync)] =
            (repository, linkId, pr) => repository.UpsertBranchPullRequestsManyAsync([(linkId, new BranchPullRequests(pr, null))]),
    };

    [Theory]
    [MemberData(nameof(ContextWriters))]
    public async Task Removing_a_context_row_another_writer_removed_first_leaves_no_row(string writer)
    {
        var race = new OtherWriterFirst();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var contextId = (await ctx.GetSpecialContextIdAsync()).Value;
        await SeedContextRowAsync(ctx, contextId, Theirs);
        race.Bind(() => DeleteContextRowAsync(ctx, contextId));

        await RunContextWriterAsync(ctx, writer, contextId, null);

        Assert.Equal(1, race.Fired);
        Assert.Empty(await ReadContextRowsAsync(ctx, contextId));
    }

    [Theory]
    [MemberData(nameof(ContextWriters))]
    public async Task Updating_a_context_row_another_writer_removed_first_stores_this_writers_pull_request(string writer)
    {
        var race = new OtherWriterFirst();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var contextId = (await ctx.GetSpecialContextIdAsync()).Value;
        await SeedContextRowAsync(ctx, contextId, Theirs);
        race.Bind(() => DeleteContextRowAsync(ctx, contextId));

        await RunContextWriterAsync(ctx, writer, contextId, Open(Mine));

        Assert.Equal(1, race.Fired);
        var row = Assert.Single(await ReadContextRowsAsync(ctx, contextId));
        Assert.Equal(Mine, row.PullRequestNumber);
        Assert.Equal("open", row.State);
    }

    [Theory]
    [MemberData(nameof(ContextWriters))]
    public async Task Adding_a_context_row_another_writer_added_first_stores_this_writers_pull_request(string writer)
    {
        var race = new OtherWriterFirst();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        var contextId = (await ctx.GetSpecialContextIdAsync()).Value;
        race.Bind(() => SeedContextRowAsync(ctx, contextId, Theirs));

        await RunContextWriterAsync(ctx, writer, contextId, Open(Mine));

        Assert.Equal(1, race.Fired);
        var row = Assert.Single(await ReadContextRowsAsync(ctx, contextId));
        Assert.Equal(Mine, row.PullRequestNumber);
        Assert.Equal("open", row.State);
    }

    // The legacy row is never removed by an Upsert (null clears its fields and keeps it), so two writers can
    // only collide when both find no row and both add one. Its key is the link id, so the second insert fails
    // the primary key.
    [Theory]
    [MemberData(nameof(LegacyWriters))]
    public async Task Adding_a_legacy_row_another_writer_added_first_stores_this_writers_pull_request(string writer)
    {
        var race = new OtherWriterFirst();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        race.Bind(() => SeedLegacyRowAsync(ctx, Theirs));

        await using (var scope = ctx.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<WorkspacePullRequestRepository>();
            await LegacyWriterByName[writer](repository, ctx.WorkspaceRepositoryId, Open(Mine));
        }

        Assert.Equal(1, race.Fired);
        var row = await ctx.ReadPullRequestAsync();
        Assert.NotNull(row);
        Assert.Equal(Mine, row!.PullRequestNumber);
        Assert.Equal("open", row.State);
    }

    [Theory]
    [MemberData(nameof(LegacyWriters))]
    public async Task Clearing_a_legacy_row_another_writer_added_first_keeps_one_cleared_row(string writer)
    {
        var race = new OtherWriterFirst();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race));
        race.Bind(() => SeedLegacyRowAsync(ctx, Theirs));

        await using (var scope = ctx.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<WorkspacePullRequestRepository>();
            await LegacyWriterByName[writer](repository, ctx.WorkspaceRepositoryId, null);
        }

        Assert.Equal(1, race.Fired);
        var row = await ctx.ReadPullRequestAsync();
        Assert.NotNull(row);
        Assert.Null(row!.PullRequestNumber);
        Assert.Null(row.State);
    }

    private static async Task RunContextWriterAsync(SyncStateTestContext ctx, string writer, int contextId, PullRequestInfo? pr)
    {
        await using var scope = ctx.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<WorkspacePullRequestRepository>();
        await ContextWriterByName[writer](repository, contextId, ctx.WorkspaceRepositoryId, pr);
    }

    private static PullRequestInfo Open(int number) => new()
    {
        Number = number,
        State = "open",
        HtmlUrl = $"https://example.test/pull/{number}",
    };

    private static async Task SeedContextRowAsync(SyncStateTestContext ctx, int contextId, int number)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        db.WorkspaceRepositoryContextPullRequests.Add(new WorkspaceRepositoryContextPullRequest
        {
            WorkspaceFeatureContextId = contextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            PullRequestNumber = number,
            State = "closed",
            LastCheckedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task DeleteContextRowAsync(SyncStateTestContext ctx, int contextId)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await db.WorkspaceRepositoryContextPullRequests
            .Where(pr => pr.WorkspaceFeatureContextId == contextId && pr.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId)
            .ExecuteDeleteAsync());
    }

    private static async Task SeedLegacyRowAsync(SyncStateTestContext ctx, int number)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        db.WorkspaceRepositoryPullRequests.Add(new WorkspaceRepositoryPullRequest
        {
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            PullRequestNumber = number,
            State = "closed",
            LastCheckedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<List<WorkspaceRepositoryContextPullRequest>> ReadContextRowsAsync(SyncStateTestContext ctx, int contextId)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.WorkspaceRepositoryContextPullRequests.AsNoTracking()
            .Where(pr => pr.WorkspaceFeatureContextId == contextId && pr.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId)
            .ToListAsync();
    }

    /// <summary>
    /// Runs the other writer once, when the first save after <see cref="Bind"/> begins. At that point the writer
    /// under test has read the row and decided what to do with it, and has written nothing yet, which is the
    /// order of a lost race. The other writer's own save comes after the flag is set and passes through.
    /// </summary>
    private sealed class OtherWriterFirst : SaveChangesInterceptor
    {
        private Func<Task>? _write;
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        /// <summary>Bound after the test seeded its rows, so a seeding save does not count as the writer under test.</summary>
        public void Bind(Func<Task> write) => _write = write;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var write = _write;
            if (write != null && Interlocked.Exchange(ref _fired, 1) == 0)
                await write();

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
