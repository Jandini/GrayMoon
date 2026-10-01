using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// Golden upgrade test: proves a real 0.1.0 database (the shipped tag) upgrades cleanly to the current schema
/// through <see cref="Migrations.RunAllAsync"/>, with the seeded 0.1.0 data surviving and the backfilled special
/// Workspace context present. The fixture at Fixtures/graymoon-0.1.0.db was produced from a temporary checkout of
/// the 0.1.0 tag (see the plan's U0-3 unit for how to regenerate it).
/// </summary>
public sealed class UpgradeFrom010Tests
{
    [Fact]
    public async Task Golden_010_database_upgrades_to_current_schema()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "graymoon-0.1.0.db");
        Assert.True(File.Exists(fixturePath), $"Fixture not found at {fixturePath}. Was it copied to the test output directory?");

        var workingCopyPath = Path.Combine(Path.GetTempPath(), $"upgrade-from-010-{Guid.NewGuid():N}.db");
        File.Copy(fixturePath, workingCopyPath, overwrite: true);

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={workingCopyPath}")
                .Options;

            await using (var db = new AppDbContext(options))
            {
                await Migrations.RunAllAsync(db);
                await AssertUpgradedDatabaseAsync(db);
            }
        }
        finally
        {
            // SQLite keeps pooled native connections open after the DbContext is disposed; clear the pool so the
            // working copy below can be deleted.
            SqliteConnection.ClearAllPools();
            if (File.Exists(workingCopyPath))
                File.Delete(workingCopyPath);
        }
    }

    private static async Task AssertUpgradedDatabaseAsync(AppDbContext db)
    {
        // Every DbSet on AppDbContext must be queryable against the upgraded schema (no "no such table" or
        // "no such column" errors), proving every migration step that touches these tables ran successfully.
        await db.Connectors.Take(1).ToListAsync();
        await db.Repositories.Take(1).ToListAsync();
        await db.Workspaces.Take(1).ToListAsync();
        await db.WorkspaceRepositories.Take(1).ToListAsync();
        await db.WorkspaceRepositoryPullRequests.Take(1).ToListAsync();
        await db.WorkspaceRepositoryActions.Take(1).ToListAsync();
        await db.WorkspaceProjects.Take(1).ToListAsync();
        await db.ProjectDependencies.Take(1).ToListAsync();
        await db.RepositoryBranches.Take(1).ToListAsync();
        await db.WorkspaceFiles.Take(1).ToListAsync();
        await db.WorkspaceFileVersionConfigs.Take(1).ToListAsync();
        await db.WorkspaceFileLineStatuses.Take(1).ToListAsync();
        await db.WorkspaceRepositoryCustomDependencies.Take(1).ToListAsync();
        await db.Settings.Take(1).ToListAsync();
        await db.WorkspaceGitRepositoryStatuses.Take(1).ToListAsync();
        await db.WorkspaceGitChangeEntries.Take(1).ToListAsync();
        await db.WorkspaceFeatures.Take(1).ToListAsync();
        await db.WorkspaceFeatureContexts.Take(1).ToListAsync();
        await db.WorkspaceFeatureRepositories.Take(1).ToListAsync();
        await db.WorkspaceRepositoryContextStates.Take(1).ToListAsync();
        await db.WorkspaceSelectedFeatureContexts.Take(1).ToListAsync();
        await db.WorkspaceRepositoryContextPullRequests.Take(1).ToListAsync();
        await db.WorkspaceRepositoryContextActions.Take(1).ToListAsync();
        await db.WorkspaceGitContextRepositoryStatuses.Take(1).ToListAsync();
        await db.WorkspaceGitContextChangeEntries.Take(1).ToListAsync();
        await db.WorkspaceFileContextStates.Take(1).ToListAsync();

        // The seeded 0.1.0 data survives the upgrade.
        var connector = Assert.Single(await db.Connectors.ToListAsync());
        Assert.Equal("gh-golden", connector.ConnectorName);
        Assert.Equal("dummy-token-0.1.0", connector.UserToken);

        var workspace = Assert.Single(await db.Workspaces.ToListAsync());
        Assert.Equal("GoldenWorkspace", workspace.Name);

        var repositories = await db.Repositories.ToListAsync();
        Assert.Equal(3, repositories.Count);

        var links = await db.WorkspaceRepositories.Where(l => l.WorkspaceId == workspace.WorkspaceId).ToListAsync();
        Assert.Equal(3, links.Count);

        var projects = await db.WorkspaceProjects.Where(p => p.WorkspaceId == workspace.WorkspaceId).ToListAsync();
        Assert.Equal(2, projects.Count);

        var dependencies = await db.ProjectDependencies.ToListAsync();
        var dependency = Assert.Single(dependencies);
        Assert.Equal("1.0.0", dependency.Version);

        // The legacy baseline backfill creates the special Workspace feature context for this Workspace.
        var workspaceContext = await db.WorkspaceFeatureContexts
            .SingleAsync(c => c.WorkspaceId == workspace.WorkspaceId && c.Kind == WorkspaceFeatureContextKind.Workspace);
        Assert.Null(workspaceContext.WorkspaceFeatureId);

        var selected = await db.WorkspaceSelectedFeatureContexts
            .SingleAsync(s => s.WorkspaceId == workspace.WorkspaceId);
        Assert.Equal(workspaceContext.WorkspaceFeatureContextId, selected.WorkspaceFeatureContextId);

        var states = await db.WorkspaceRepositoryContextStates
            .Where(s => s.WorkspaceFeatureContextId == workspaceContext.WorkspaceFeatureContextId)
            .ToListAsync();
        Assert.Equal(3, states.Count);
    }
}
