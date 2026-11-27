using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// A repository that was just added to a Workspace has no state row in the context yet. The first writers of that
/// row run on different scopes (the hook sync queue, a sync, a pull, the single-repository refresh), and each one
/// decides "no row" from its own read. The one that saved second failed the unique index on (context, repository),
/// and that failed the hook notification or the operation. The loser must end with the stored row holding what it
/// wrote, and with what the winner wrote in the groups the loser did not probe.
/// </summary>
public sealed class ContextStateRowCreationRaceTests
{
    private const string Branch = "feature/y";
    private const string Head = "1111111111111111111111111111111111111111";

    [Fact]
    public async Task A_single_state_write_keeps_its_values_when_another_writer_stored_the_first_row()
    {
        var race = new RunWhenStateRowIsAboutToBeAdded();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race, race.BeforeTransaction));
        var contextId = await StartWithoutStateRowAsync(ctx);
        race.Bind(() => ApplyFromAnotherScopeAsync(ctx, contextId, HookSnapshot()));

        RepositoryStateWriteResult result;
        await using (var scope = ctx.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
            result = await writer.ApplyAsync(contextId, ctx.WorkspaceId, ctx.RepositoryId, IdentitySnapshot());
        }

        Assert.Equal(1, race.Fired);
        Assert.True(result.Applied);
        // Compared with the row the other writer stored: the identity is new, nothing else was probed.
        Assert.Equal(RepositoryFactGroups.Identity, result.Changed);
        await AssertBothWritesStoredAsync(ctx, contextId);
    }

    [Fact]
    public async Task A_write_that_lost_the_race_to_the_same_values_reports_no_change()
    {
        var race = new RunWhenStateRowIsAboutToBeAdded();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race, race.BeforeTransaction));
        var contextId = await StartWithoutStateRowAsync(ctx);
        race.Bind(() => ApplyFromAnotherScopeAsync(ctx, contextId, IdentitySnapshot()));

        RepositoryStateWriteResult result;
        await using (var scope = ctx.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
            result = await writer.ApplyAsync(contextId, ctx.WorkspaceId, ctx.RepositoryId, IdentitySnapshot());
        }

        Assert.Equal(1, race.Fired);
        Assert.True(result.Applied);
        Assert.Equal(RepositoryFactGroups.Identity, result.Probed);
        // The other writer stored exactly this: what follows a change has nothing to follow.
        Assert.Equal(RepositoryFactGroups.None, result.Changed);
        var state = await ReadStateAsync(ctx, contextId);
        Assert.Equal(Branch, state.BranchName);
        Assert.Equal(Head, state.HeadCommit);
    }

    [Fact]
    public async Task What_was_staged_for_a_new_row_and_does_not_apply_to_the_stored_one_is_not_written()
    {
        // The other writer stored a tag checkout. Commit counts are not kept for one, so this write, which staged
        // its counts on its own new row and on the link, must leave both without counts, as a later write would.
        var race = new RunWhenStateRowIsAboutToBeAdded();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race, race.BeforeTransaction));
        var contextId = await StartWithoutStateRowAsync(ctx);
        race.Bind(() => ApplyFromAnotherScopeAsync(
            ctx, contextId, new RepositoryStateSnapshot { IdentityProbed = true, CheckedOutTag = "v1", HeadCommit = Head }));

        RepositoryStateWriteResult result;
        await using (var scope = ctx.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
            result = await writer.ApplyAsync(
                contextId,
                ctx.WorkspaceId,
                ctx.RepositoryId,
                new RepositoryStateSnapshot { CommitCountsProbed = true, OutgoingCommits = 5, IncomingCommits = 1 });
        }

        Assert.Equal(1, race.Fired);
        Assert.True(result.Applied);
        Assert.Equal(RepositoryFactGroups.None, result.Changed);
        var state = await ReadStateAsync(ctx, contextId);
        Assert.Equal("v1", state.CheckedOutTag);
        Assert.Null(state.OutgoingCommits);
        Assert.Null(state.IncomingCommits);
        var link = await ctx.ReadLinkAsync();
        Assert.Equal("v1", link.CheckedOutTag);
        Assert.Null(link.OutgoingCommits);
        Assert.Null(link.IncomingCommits);
    }

    [Fact]
    public async Task A_tag_refresh_keeps_its_values_when_another_writer_stored_the_first_row()
    {
        var race = new RunWhenStateRowIsAboutToBeAdded();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race, race.BeforeTransaction));
        var contextId = await StartWithoutStateRowAsync(ctx);
        race.Bind(() => ApplyFromAnotherScopeAsync(ctx, contextId, HookSnapshot()));

        await using (var scope = ctx.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
            Assert.True(await writer.ApplyTagRefreshAsync(contextId, ctx.WorkspaceRepositoryId, ["v2", "v1"], "v1"));
        }

        Assert.Equal(1, race.Fired);
        var state = await ReadStateAsync(ctx, contextId);
        Assert.Equal("v1", state.CheckedOutTag);
        Assert.True(state.HasNewerTag);
        // The tag refresh says nothing about the version: the other writer's stays.
        Assert.Equal("2.0.0", state.GitVersion);
        var link = await ctx.ReadLinkAsync();
        Assert.Equal("v1", link.CheckedOutTag);
        Assert.Equal("2.0.0", link.GitVersion);
    }

    [Fact]
    public async Task A_batch_state_write_keeps_its_values_when_another_writer_stored_the_first_row()
    {
        var race = new RunWhenStateRowIsAboutToBeAdded();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race, race.BeforeTransaction));
        var contextId = await StartWithoutStateRowAsync(ctx);
        race.Bind(() => ApplyFromAnotherScopeAsync(ctx, contextId, HookSnapshot()));

        IReadOnlyDictionary<int, RepositoryStateWriteResult> results;
        await using (var scope = ctx.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
            results = await writer.ApplyBatchAsync(contextId, ctx.WorkspaceId, [(ctx.RepositoryId, IdentitySnapshot())]);
        }

        Assert.Equal(1, race.Fired);
        Assert.True(results[ctx.RepositoryId].Applied);
        Assert.Equal(RepositoryFactGroups.Identity, results[ctx.RepositoryId].Changed);
        await AssertBothWritesStoredAsync(ctx, contextId);
    }

    [Fact]
    public async Task A_batch_state_write_with_projects_and_branches_keeps_all_of_it_when_another_writer_stored_the_first_row()
    {
        // This batch saves inside the project reconcile, together with the project rows and the branch rows.
        var race = new RunWhenStateRowIsAboutToBeAdded();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o => o.AddInterceptors(race, race.BeforeTransaction));
        await ctx.UseDotNetDependencyProfileAsync();
        var contextId = await StartWithoutStateRowAsync(ctx);
        race.Bind(() => ApplyFromAnotherScopeAsync(ctx, contextId, HookSnapshot()));

        var snapshot = new RepositoryStateSnapshot
        {
            IdentityProbed = true,
            BranchName = Branch,
            HeadCommit = Head,
            BranchesProbed = true,
            LocalBranches = ["main", Branch],
            RemoteBranches = ["main"],
            DefaultBranchName = "main",
            ProjectsProbed = true,
            Projects =
            [
                new RepositorySyncProjectNotification
                {
                    Name = "GrayMoon.Api",
                    ProjectType = (int)ProjectType.Service,
                    ProjectPath = "src/GrayMoon.Api/GrayMoon.Api.csproj",
                    TargetFramework = "net10.0",
                },
            ],
        };

        IReadOnlyDictionary<int, RepositoryStateWriteResult> results;
        await using (var scope = ctx.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
            results = await writer.ApplyBatchAsync(contextId, ctx.WorkspaceId, [(ctx.RepositoryId, snapshot)]);
        }

        Assert.Equal(1, race.Fired);
        var changed = results[ctx.RepositoryId].Changed;
        Assert.True(changed.HasFlag(RepositoryFactGroups.Identity));
        Assert.True(changed.HasFlag(RepositoryFactGroups.Branches));
        Assert.True(changed.HasFlag(RepositoryFactGroups.Projects));
        await AssertBothWritesStoredAsync(ctx, contextId);

        var state = await ReadStateAsync(ctx, contextId);
        Assert.Equal(1, state.Projects);
        Assert.Equal(ProjectType.Service, state.RepositoryType);
        Assert.Equal("GrayMoon.Api", Assert.Single(await ctx.ReadProjectsAsync()).ProjectName);
        var branches = await ctx.ReadBranchesAsync();
        Assert.Equal(1, branches.Count(b => b.BranchName == Branch && !b.IsRemote && !b.IsTag));
        Assert.Equal(1, branches.Count(b => b.BranchName == "main" && !b.IsRemote && b.IsDefault));
        Assert.Equal(1, branches.Count(b => b.BranchName == "origin/main" && b.IsRemote));
    }

    /// <summary>What the writer under test stores: the checkout's identity and nothing else.</summary>
    private static RepositoryStateSnapshot IdentitySnapshot() => new()
    {
        IdentityProbed = true,
        BranchName = Branch,
        HeadCommit = Head,
    };

    /// <summary>What the other writer stores: groups the writer under test does not probe.</summary>
    private static RepositoryStateSnapshot HookSnapshot() => new()
    {
        GitVersionProbed = true,
        GitVersion = "2.0.0",
        CommitCountsProbed = true,
        OutgoingCommits = 7,
        IncomingCommits = 0,
    };

    private static async Task AssertBothWritesStoredAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        var state = await ReadStateAsync(ctx, contextId);
        Assert.Equal(Branch, state.BranchName);
        Assert.Equal(Head, state.HeadCommit);
        Assert.Equal("2.0.0", state.GitVersion);
        Assert.Equal(7, state.OutgoingCommits);

        // The special Workspace mirrors onto the link: neither writer's part of it is lost there.
        var link = await ctx.ReadLinkAsync();
        Assert.Equal(Branch, link.BranchName);
        Assert.Equal("2.0.0", link.GitVersion);
        Assert.Equal(7, link.OutgoingCommits);
    }

    /// <summary>
    /// The test database is seeded through the migration backfill, which stores a state row for every link. A
    /// repository added to a Workspace afterwards has none until its first state write: that is the state here.
    /// </summary>
    private static async Task<WorkspaceFeatureContextId> StartWithoutStateRowAsync(SyncStateTestContext ctx)
    {
        var contextId = await ctx.GetSpecialContextIdAsync();
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await db.WorkspaceRepositoryContextStates
            .Where(s => s.WorkspaceFeatureContextId == contextId.Value && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId)
            .ExecuteDeleteAsync();
        return contextId;
    }

    private static async Task ApplyFromAnotherScopeAsync(
        SyncStateTestContext ctx,
        WorkspaceFeatureContextId contextId,
        RepositoryStateSnapshot snapshot)
    {
        await using var scope = ctx.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
        Assert.True(await writer.ApplyAsync(contextId, ctx.WorkspaceId, ctx.RepositoryId, snapshot));
    }

    /// <summary>Reads the one state row. More than one, or none, fails the test.</summary>
    private static async Task<WorkspaceRepositoryContextState> ReadStateAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == contextId.Value && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
    }

    /// <summary>
    /// On the first save that adds a state row, runs the other writer to its end first: the writer under test has
    /// decided "no row" and has not sent its insert, which is the order in which it loses the race. The other
    /// writer's own save adds a state row too; it is not interrupted.
    /// <para>
    /// A save that also adds branch rows opens its transaction, and with it takes the write lock, before EF Core
    /// reports the save. <see cref="BeforeTransaction"/> runs the other writer ahead of that, while it can still commit.
    /// </para>
    /// </summary>
    private sealed class RunWhenStateRowIsAboutToBeAdded : SaveChangesInterceptor
    {
        private Func<Task>? _otherWriter;
        private int _fired;

        public RunWhenStateRowIsAboutToBeAdded() => BeforeTransaction = new TransactionStarting(this);

        public DbTransactionInterceptor BeforeTransaction { get; }

        public int Fired => Volatile.Read(ref _fired);

        public void Bind(Func<Task> otherWriter) => _otherWriter = otherWriter;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await RunOtherWriterOnceAsync(eventData.Context);
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private async Task RunOtherWriterOnceAsync(DbContext? context)
        {
            if (_otherWriter is { } otherWriter
                && context is AppDbContext db
                && db.ChangeTracker.Entries<WorkspaceRepositoryContextState>().Any(entry => entry.State == EntityState.Added)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await otherWriter();
            }
        }

        private sealed class TransactionStarting(RunWhenStateRowIsAboutToBeAdded race) : DbTransactionInterceptor
        {
            public override async ValueTask<InterceptionResult<System.Data.Common.DbTransaction>> TransactionStartingAsync(
                System.Data.Common.DbConnection connection,
                TransactionStartingEventData eventData,
                InterceptionResult<System.Data.Common.DbTransaction> result,
                CancellationToken cancellationToken = default)
            {
                await race.RunOtherWriterOnceAsync(eventData.Context);
                return await base.TransactionStartingAsync(connection, eventData, result, cancellationToken);
            }
        }
    }
}
