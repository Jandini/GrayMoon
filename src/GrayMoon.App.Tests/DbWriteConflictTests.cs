using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// The shared answer to "two writers read, decided and saved the same row": the loser reads again and decides again.
/// The exceptions here are produced by a real database, not constructed.
/// </summary>
public sealed class DbWriteConflictTests
{
    [Fact]
    public async Task A_row_added_by_another_writer_first_is_a_lost_race_and_the_second_attempt_sees_it()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        var attempts = 0;

        await DbWriteConflict.RetryAsync(async () =>
        {
            attempts++;
            await using var db = await factory.CreateDbContextAsync();
            if (await db.Settings.AnyAsync(s => s.Key == "race"))
                return;

            // Decided "no row yet"; another writer stores it before this save.
            await using (var other = await factory.CreateDbContextAsync())
            {
                other.Settings.Add(new Setting { Key = "race", Value = "other" });
                await other.SaveChangesAsync();
            }

            db.Settings.Add(new Setting { Key = "race", Value = "mine" });
            await db.SaveChangesAsync();
        });

        Assert.Equal(2, attempts);
        await using var read = await factory.CreateDbContextAsync();
        Assert.Equal("other", (await read.Settings.SingleAsync(s => s.Key == "race")).Value);
    }

    [Fact]
    public async Task A_row_removed_by_another_writer_first_is_a_lost_race()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Settings.Add(new Setting { Key = "gone", Value = "v" });
            await seed.SaveChangesAsync();
        }

        var attempts = 0;
        var found = await DbWriteConflict.RetryAsync(async () =>
        {
            attempts++;
            await using var db = await factory.CreateDbContextAsync();
            var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == "gone");
            if (row is null)
                return false;

            await using (var other = await factory.CreateDbContextAsync())
                await other.Settings.Where(s => s.Key == "gone").ExecuteDeleteAsync();

            row.Value = "changed";
            await db.SaveChangesAsync();
            return true;
        });

        Assert.Equal(2, attempts);
        Assert.False(found);
    }

    [Fact]
    public async Task A_race_that_is_lost_every_time_is_reported_after_the_last_attempt()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Settings.Add(new Setting { Key = "taken", Value = "v" });
            await seed.SaveChangesAsync();
        }

        var attempts = 0;
        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => DbWriteConflict.RetryAsync(async () =>
        {
            attempts++;
            await using var db = await factory.CreateDbContextAsync();
            db.Settings.Add(new Setting { Key = "taken", Value = "again" });
            await db.SaveChangesAsync();
        }));

        Assert.Equal(DbWriteConflict.MaxAttempts, attempts);
        Assert.True(DbWriteConflict.IsUniqueViolation(ex));
        Assert.True(DbWriteConflict.IsLostRace(ex));
    }

    [Fact]
    public async Task Any_other_failure_is_not_retried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => DbWriteConflict.RetryAsync(() =>
        {
            attempts++;
            throw new InvalidOperationException("not a lost race");
        }));

        Assert.Equal(1, attempts);
        Assert.False(DbWriteConflict.IsLostRace(new DbUpdateException("a constraint that is not a unique index", new InvalidOperationException())));
    }
}
