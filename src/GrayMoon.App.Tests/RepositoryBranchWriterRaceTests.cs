using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// A workspace checkout runs every repository at once. Each checkout inserts the new local branch, and the
/// checkout hook sync inserts that same branch. The later insert must keep the row already stored.
/// </summary>
public sealed class RepositoryBranchWriterRaceTests
{
    private const string BranchName = "local-development";

    [Fact]
    public async Task Add_keeps_the_branch_when_the_checkout_sync_inserts_it_first()
    {
        var race = new InsertSameBranchAfterTheWritersRead();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: options => options.AddInterceptors(race));
        race.Bind(ctx.Resolve<IDbContextFactory<AppDbContext>>(), ctx.WorkspaceRepositoryId);

        await using var scope = ctx.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<RepositoryBranchWriter>();
        race.Arm();
        var added = await writer.AddLocalBranchesAsync([(ctx.WorkspaceRepositoryId, BranchName)]);

        Assert.Equal(0, added);
        Assert.True(race.Inserted);
        Assert.Equal(1, await CountBranchAsync(ctx, BranchName, isRemote: false));
    }

    [Fact]
    public async Task Full_refresh_still_updates_other_rows_when_the_new_branch_was_just_inserted()
    {
        var race = new InsertSameBranchAfterTheWritersRead();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: options => options.AddInterceptors(race));
        race.Bind(ctx.Resolve<IDbContextFactory<AppDbContext>>(), ctx.WorkspaceRepositoryId);

        await using (var seed = ctx.CreateScope())
        {
            var writer = seed.ServiceProvider.GetRequiredService<RepositoryBranchWriter>();
            await writer.PersistAsync(ctx.WorkspaceRepositoryId, ["main"], ["main"], "main", tags: null);
        }

        var stale = DateTime.UtcNow.AddDays(-2);
        await using (var edit = ctx.CreateScope())
        {
            var db = edit.ServiceProvider.GetRequiredService<AppDbContext>();
            var main = await db.RepositoryBranches.SingleAsync(branch => branch.BranchName == "main" && !branch.IsRemote);
            main.LastSeenAt = stale;
            await db.SaveChangesAsync();
        }

        await using var scope = ctx.CreateScope();
        var refresh = scope.ServiceProvider.GetRequiredService<RepositoryBranchWriter>();
        race.Arm();
        await refresh.PersistAsync(
            ctx.WorkspaceRepositoryId,
            ["main", BranchName],
            ["main"],
            "main",
            tags: null);

        var rows = await ctx.ReadBranchesAsync();
        Assert.True(race.Inserted);
        Assert.Equal(1, rows.Count(branch => branch.BranchName == BranchName && !branch.IsRemote && !branch.IsTag));
        var storedMain = rows.Single(branch => branch.BranchName == "main" && !branch.IsRemote);
        Assert.True(storedMain.IsDefault);
        Assert.True(storedMain.LastSeenAt > stale);
    }

    // The handled race used to reach the database as an insert that failed, which EF Core logs as two errors
    // with a stack trace (App log of 2026-11-25, twice during one Create Feature). A row stored after the
    // writer decided to add it, but before the save, is now dropped without sending the insert.
    [Fact]
    public async Task A_branch_stored_before_the_save_is_dropped_without_a_failed_insert()
    {
        var failures = new CountFailedCommands();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: options => options.AddInterceptors(failures));

        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.RepositoryBranches.Add(NewBranch(ctx.WorkspaceRepositoryId));

        await using (var other = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            other.RepositoryBranches.Add(NewBranch(ctx.WorkspaceRepositoryId));
            await other.SaveChangesAsync();
        }

        var dropped = await db.SaveChangesCountingStoredBranchRowsAsync();

        Assert.Equal(1, dropped);
        Assert.Equal(0, failures.Count);
        Assert.Equal(1, await CountBranchAsync(ctx, BranchName, isRemote: false));
    }

    // The check above still left a moment between its read and the insert in which another writer could commit
    // the same row (App log of 2026-11-26, 10:36:45, once during a Create Feature). The save now takes the
    // database write lock before the check and keeps it until the insert is committed, so no writer can get in
    // between. Runs on a database file in WAL mode like the App's, because a shared-cache memory database locks
    // differently.
    [Fact]
    public async Task A_save_that_adds_a_branch_holds_the_write_lock_from_its_check_until_its_insert_is_committed()
    {
        var directory = Directory.CreateTempSubdirectory("graymoon-branch-race-").FullName;
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "race.db")};Pooling=False;Foreign Keys=False";
            var competitor = new InsertSameBranchDuringStoredRowCheck(connectionString);
            var failures = new CountFailedCommands();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(competitor, failures)
                .Options;
            await using var db = new AppDbContext(options);
            await db.Database.CreateSchemaAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            db.RepositoryBranches.Add(NewBranch(workspaceRepositoryId: 1));

            competitor.Arm();
            var dropped = await db.SaveChangesCountingStoredBranchRowsAsync();

            Assert.Equal(InsertSameBranchDuringStoredRowCheck.Busy, competitor.ResultCode);
            Assert.Equal(0, failures.Count);
            Assert.Equal(0, dropped);
            Assert.Equal(1, await db.RepositoryBranches.CountAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RepositoryBranch NewBranch(int workspaceRepositoryId) => new()
    {
        WorkspaceRepositoryId = workspaceRepositoryId,
        BranchName = BranchName,
        IsRemote = false,
        LastSeenAt = DateTime.UtcNow,
        IsDefault = false
    };

    private sealed class CountFailedCommands : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override Task CommandFailedAsync(
            System.Data.Common.DbCommand command,
            CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// When the save reads which added branch rows are already stored, another connection tries once, without
    /// waiting, to insert the same row.
    /// </summary>
    private sealed class InsertSameBranchDuringStoredRowCheck(string connectionString) : DbCommandInterceptor
    {
        /// <summary>SQLITE_BUSY: another connection holds the write lock.</summary>
        public const int Busy = 5;

        private int _armed;

        /// <summary>The SQLite result code of the competing insert; null when it was not tried.</summary>
        public int? ResultCode { get; private set; }

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command,
            CommandExecutedEventData eventData,
            System.Data.Common.DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"RepositoryBranches\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                using var other = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                other.Open();
                // sqlite3_exec directly: SqliteCommand would retry a busy database until its timeout.
                ResultCode = SQLitePCL.raw.sqlite3_exec(
                    other.Handle,
                    "INSERT INTO \"RepositoryBranches\" (\"BranchName\", \"IsDefault\", \"IsRemote\", \"IsTag\", \"LastSeenAt\", \"SortIndex\", \"WorkspaceRepositoryId\") "
                    + $"VALUES ('{BranchName}', 0, 0, 0, '2026-11-26 00:00:00', 0, 1);");
            }

            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task<int> CountBranchAsync(SyncStateTestContext ctx, string branchName, bool isRemote)
    {
        var rows = await ctx.ReadBranchesAsync();
        return rows.Count(branch => branch.BranchName == branchName && branch.IsRemote == isRemote && !branch.IsTag);
    }

    /// <summary>
    /// Once armed, commits the local branch <see cref="BranchName"/> from another context when the next save is
    /// about to open its transaction: the writer has then decided "no row yet" and the save does not hold the
    /// write lock yet, the same order as the checkout hook sync winning the race.
    /// </summary>
    private sealed class InsertSameBranchAfterTheWritersRead : DbTransactionInterceptor
    {
        private IDbContextFactory<AppDbContext>? _factory;
        private int _workspaceRepositoryId;
        private int _armed;

        public bool Inserted { get; private set; }

        public void Bind(IDbContextFactory<AppDbContext> factory, int workspaceRepositoryId)
        {
            _factory = factory;
            _workspaceRepositoryId = workspaceRepositoryId;
        }

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override async ValueTask<InterceptionResult<System.Data.Common.DbTransaction>> TransactionStartingAsync(
            System.Data.Common.DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<System.Data.Common.DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (_factory != null && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                await using var other = await _factory.CreateDbContextAsync(cancellationToken);
                other.RepositoryBranches.Add(NewBranch(_workspaceRepositoryId));
                await other.SaveChangesAsync(cancellationToken);
                Inserted = true;
            }

            return await base.TransactionStartingAsync(connection, eventData, result, cancellationToken);
        }
    }
}
