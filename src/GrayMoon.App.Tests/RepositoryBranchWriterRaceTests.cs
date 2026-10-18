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
        var race = new InsertSameBranchBeforeSave();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: options => options.AddInterceptors(race));
        race.Bind(ctx.Resolve<IDbContextFactory<AppDbContext>>());

        await using var scope = ctx.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<RepositoryBranchWriter>();
        var added = await writer.AddLocalBranchesAsync([(ctx.WorkspaceRepositoryId, BranchName)]);

        Assert.Equal(0, added);
        Assert.Equal(1, await CountBranchAsync(ctx, BranchName, isRemote: false));
    }

    [Fact]
    public async Task Full_refresh_still_updates_other_rows_when_the_new_branch_was_just_inserted()
    {
        var race = new InsertSameBranchBeforeSave();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: options => options.AddInterceptors(race));
        race.Bind(ctx.Resolve<IDbContextFactory<AppDbContext>>());

        await using (var seed = ctx.CreateScope())
        {
            var writer = seed.ServiceProvider.GetRequiredService<RepositoryBranchWriter>();
            await writer.PersistAsync(ctx.WorkspaceRepositoryId, ["main"], ["main"], "main", tags: null, currentTag: null);
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
        await refresh.PersistAsync(
            ctx.WorkspaceRepositoryId,
            ["main", BranchName],
            ["main"],
            "main",
            tags: null,
            currentTag: null);

        var rows = await ctx.ReadBranchesAsync();
        Assert.Equal(1, rows.Count(branch => branch.BranchName == BranchName && !branch.IsRemote && !branch.IsTag));
        var storedMain = rows.Single(branch => branch.BranchName == "main" && !branch.IsRemote);
        Assert.True(storedMain.IsDefault);
        Assert.True(storedMain.LastSeenAt > stale);
    }

    private static async Task<int> CountBranchAsync(SyncStateTestContext ctx, string branchName, bool isRemote)
    {
        var rows = await ctx.ReadBranchesAsync();
        return rows.Count(branch => branch.BranchName == branchName && branch.IsRemote == isRemote && !branch.IsTag);
    }

    /// <summary>
    /// On the first save that adds <see cref="BranchName"/>, commits that row from another context first,
    /// the same order as the checkout hook sync winning the race.
    /// </summary>
    private sealed class InsertSameBranchBeforeSave : SaveChangesInterceptor
    {
        private IDbContextFactory<AppDbContext>? _factory;
        private int _armed;

        public void Bind(IDbContextFactory<AppDbContext> factory) => _factory = factory;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_factory != null
                && eventData.Context is AppDbContext db
                && Interlocked.Exchange(ref _armed, 1) == 0)
            {
                var pending = db.ChangeTracker.Entries<RepositoryBranch>()
                    .Where(entry => entry.State == EntityState.Added && entry.Entity.BranchName == BranchName)
                    .Select(entry => entry.Entity)
                    .ToList();
                if (pending.Count == 0)
                {
                    Interlocked.Exchange(ref _armed, 0);
                }
                else
                {
                    await using var other = await _factory.CreateDbContextAsync(cancellationToken);
                    foreach (var branch in pending)
                    {
                        other.RepositoryBranches.Add(new RepositoryBranch
                        {
                            WorkspaceRepositoryId = branch.WorkspaceRepositoryId,
                            BranchName = branch.BranchName,
                            IsRemote = branch.IsRemote,
                            IsTag = branch.IsTag,
                            LastSeenAt = DateTime.UtcNow,
                            IsDefault = false
                        });
                    }

                    await other.SaveChangesAsync(cancellationToken);
                }
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
