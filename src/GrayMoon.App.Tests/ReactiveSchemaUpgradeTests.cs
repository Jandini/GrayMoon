using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// The upgrade of a database from before the reactive work (schema version 22) through the whole migration runner,
/// as the App runs it at startup. The database is the kind a user has: a Workspace with a Feature whose removal
/// was interrupted, and a repository that inherited its parent's version. Steps 23 and 24 add one column each.
/// </summary>
public sealed class ReactiveSchemaUpgradeTests
{
    [Fact]
    public async Task A_version_22_database_with_an_interrupted_removal_upgrades_and_keeps_every_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.CreateSchemaAsync();
        var seeded = await SeedAsync(db);
        await ExecuteAsync(connection, """
            ALTER TABLE "WorkspaceFeatureRepositories" DROP COLUMN "RemovalOutcomeJson";
            ALTER TABLE "WorkspaceRepositoryContextStates" DROP COLUMN "EffectiveVersionEvidenceKey";
            PRAGMA user_version = 22;
            """);

        await Migrations.RunAllAsync(db, NullLogger.Instance, null, _ => Task.FromResult(true));
        // A second start finds nothing to do.
        await Migrations.RunAllAsync(db, NullLogger.Instance, null, _ => Task.FromResult(true));

        Assert.Equal((long)Migrations.StrictSteps.Max(s => s.Version), await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('WorkspaceFeatureRepositories') WHERE name = 'RemovalOutcomeJson'"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositoryContextStates') WHERE name = 'EffectiveVersionEvidenceKey'"));

        db.ChangeTracker.Clear();
        var feature = await db.WorkspaceFeatures.AsNoTracking().SingleAsync();
        Assert.Equal((WorkspaceFeatureLifecycleState.NeedsRepair, "Interrupted while removing"), (feature.LifecycleState, feature.LastError));
        var rows = await db.WorkspaceFeatureRepositories.AsNoTracking().OrderBy(r => r.WorkspaceFeatureRepositoryId).ToListAsync();
        Assert.Equal([WorkspaceFeatureRepositoryState.Removed, WorkspaceFeatureRepositoryState.Removing], rows.Select(r => r.State));
        Assert.Equal([@"C:\features\feat\api", @"C:\features\feat\web"], rows.Select(r => r.WorktreePath));
        Assert.All(rows, r => Assert.Null(r.RemovalOutcomeJson));
        var state = await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == seeded.FeatureContextId && s.WorkspaceRepositoryId == seeded.LinkIds[1]);
        Assert.Equal(
            ("2.4.0-beta.2", (int)EffectiveVersionKind.InheritedVerified, "def456", EffectiveVersionReasonCodes.Verified),
            (state.EffectiveVersion, state.EffectiveVersionKind!.Value, state.EffectiveVersionCommitSha, state.EffectiveVersionReasonCode));
        Assert.Null(state.EffectiveVersionEvidenceKey);

        // The upgraded database takes what the new code writes.
        var tracked = await db.WorkspaceFeatureRepositories.SingleAsync(r => r.State == WorkspaceFeatureRepositoryState.Removing);
        tracked.RemovalOutcomeJson = """{"worktree":"Blocked"}""";
        var trackedState = await db.WorkspaceRepositoryContextStates.SingleAsync(s => s.WorkspaceRepositoryContextStateId == state.WorkspaceRepositoryContextStateId);
        trackedState.EffectiveVersionEvidenceKey = "0123456789ABCDEF0123456789ABCDEF";
        await db.SaveChangesAsync();
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM WorkspaceFeatureRepositories WHERE RemovalOutcomeJson IS NOT NULL"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM WorkspaceRepositoryContextStates WHERE EffectiveVersionEvidenceKey IS NOT NULL"));
    }

    private sealed record Seeded(int FeatureContextId, IReadOnlyList<int> LinkIds);

    private static async Task<Seeded> SeedAsync(AppDbContext db)
    {
        var connector = new Connector { ConnectorName = "github", ConnectorType = ConnectorType.GitHub, ApiBaseUrl = "https://api.github.com", IsActive = true };
        db.Connectors.Add(connector);
        var workspace = new Workspace { Name = "ws", RootPath = @"C:\ws" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var repositories = new[] { "api", "web" }
            .Select(name => new Repository { ConnectorId = connector.ConnectorId, RepositoryName = name, OrgName = "acme", Visibility = "Public", CloneUrl = $"https://github.com/acme/{name}.git" })
            .ToList();
        db.Repositories.AddRange(repositories);
        await db.SaveChangesAsync();
        var links = repositories
            .Select(r => new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = r.RepositoryId, DefaultBranchName = "main" })
            .ToList();
        db.WorkspaceRepositories.AddRange(links);
        var feature = new WorkspaceFeature
        {
            WorkspaceId = workspace.WorkspaceId,
            Name = "feat",
            LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair,
            LastError = "Interrupted while removing",
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();
        var special = new WorkspaceFeatureContext { WorkspaceId = workspace.WorkspaceId, Kind = WorkspaceFeatureContextKind.Workspace, CreatedAt = DateTime.UtcNow };
        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = workspace.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatureContexts.AddRange(special, context);
        await db.SaveChangesAsync();
        db.WorkspaceFeatureRepositories.AddRange(
            new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = links[0].WorkspaceRepositoryId,
                WorktreePath = @"C:\features\feat\api",
                BaseCommitSha = "abc123",
                State = WorkspaceFeatureRepositoryState.Removed,
                CreatedAt = DateTime.UtcNow,
            },
            new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = links[1].WorkspaceRepositoryId,
                WorktreePath = @"C:\features\feat\web",
                BaseCommitSha = "def456",
                State = WorkspaceFeatureRepositoryState.Removing,
                LastError = "web: changed since the check, run Remove again.",
                CreatedAt = DateTime.UtcNow,
            });
        foreach (var contextId in new[] { special.WorkspaceFeatureContextId, context.WorkspaceFeatureContextId })
        {
            foreach (var link in links)
            {
                db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
                {
                    WorkspaceFeatureContextId = contextId,
                    WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                    BranchName = contextId == special.WorkspaceFeatureContextId ? "main" : "feat",
                    HeadCommit = "def456",
                    EffectiveVersion = "2.4.0-beta.2",
                    EffectiveVersionKind = (int)EffectiveVersionKind.InheritedVerified,
                    EffectiveVersionCommitSha = "def456",
                    EffectiveVersionReasonCode = EffectiveVersionReasonCodes.Verified,
                });
            }
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seeded(context.WorkspaceFeatureContextId, links.Select(l => l.WorkspaceRepositoryId).ToList());
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
