using GrayMoon.App.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// Upgrading a real 0.1.0 database (the checked-in fixture): the legacy baseline's context backfill throws
/// part-way on that schema, so strict step 16 must finish the job for every Workspace.
/// </summary>
public class SpecialWorkspaceContextBackfillRepairTests
{
    private const int FirstWorkspaceId = 9001;
    private const int SecondWorkspaceId = 9002;

    [Fact]
    public async Task Upgrading_a_010_database_copies_Git_Changes_into_the_context_tables_for_every_workspace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backfill-repair-{Guid.NewGuid():N}.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graymoon-0.1.0.db"), path, overwrite: true);
        try
        {
            await SeedTwoWorkspacesWithGitChangesAsync(path);

            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
            await using var db = new AppDbContext(options);
            await Migrations.RunAllAsync(db);

            foreach (var workspaceId in new[] { FirstWorkspaceId, SecondWorkspaceId })
            {
                var contextId = await db.WorkspaceFeatureContexts.AsNoTracking()
                    .Where(c => c.WorkspaceId == workspaceId && c.Kind == Models.WorkspaceFeatureContextKind.Workspace)
                    .Select(c => c.WorkspaceFeatureContextId)
                    .SingleAsync();

                Assert.Equal(1, await db.WorkspaceGitContextRepositoryStatuses.AsNoTracking()
                    .CountAsync(s => s.WorkspaceFeatureContextId == contextId));
                Assert.Equal(3, await db.WorkspaceGitContextChangeEntries.AsNoTracking()
                    .CountAsync(e => e.WorkspaceFeatureContextId == contextId));
                Assert.True(await db.WorkspaceSelectedFeatureContexts.AsNoTracking().AnyAsync(s => s.WorkspaceId == workspaceId));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Running_the_repair_again_does_not_duplicate_anything()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backfill-repair-{Guid.NewGuid():N}.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graymoon-0.1.0.db"), path, overwrite: true);
        try
        {
            await SeedTwoWorkspacesWithGitChangesAsync(path);

            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
            await using var db = new AppDbContext(options);
            await Migrations.RunAllAsync(db);
            var entriesBefore = await db.WorkspaceGitContextChangeEntries.AsNoTracking().CountAsync();
            var contextsBefore = await db.WorkspaceFeatureContexts.AsNoTracking().CountAsync();

            await Migrations.MigrateSpecialWorkspaceContextBackfillRepairAsync(db);

            Assert.Equal(entriesBefore, await db.WorkspaceGitContextChangeEntries.AsNoTracking().CountAsync());
            Assert.Equal(contextsBefore, await db.WorkspaceFeatureContexts.AsNoTracking().CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(path);
        }
    }

    private static async Task SeedTwoWorkspacesWithGitChangesAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();

        async Task Execute(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        await Execute("""
            INSERT OR IGNORE INTO "Connectors" ("ConnectorId","ConnectorName","ConnectorType","ApiBaseUrl","Status","IsActive","IsHealthy")
            VALUES (9000,'gh',0,'https://api.github.com','ok',1,1)
            """);

        foreach (var (workspaceId, index) in new[] { (FirstWorkspaceId, 0), (SecondWorkspaceId, 1) })
        {
            var linkId = workspaceId;
            await Execute($"""
                INSERT INTO "Workspaces" ("WorkspaceId","Name","IsDefault","RootPath","IsInSync","ExcludeAiWorkflows")
                VALUES ({workspaceId},'ws{workspaceId}',0,'C:\\ws{workspaceId}',1,0)
                """);
            await Execute($"""
                INSERT INTO "Repositories" ("RepositoryId","ConnectorId","RepositoryName","Visibility","Archived","CloneUrl","GitHubRepositoryId")
                VALUES ({workspaceId},9000,'repo{index}','public',0,'https://github.com/o/repo{index}.git',{workspaceId})
                """);
            await Execute($"""
                INSERT INTO "WorkspaceRepositories" ("WorkspaceRepositoryId","WorkspaceId","RepositoryId","BranchName","SyncStatus")
                VALUES ({linkId},{workspaceId},{workspaceId},'main',0)
                """);
            await Execute($"""
                INSERT INTO "WorkspaceGitRepositoryStatus"
                    ("WorkspaceRepositoryId","SnapshotVersion","BranchName","IsDetachedHead","IsUnbornBranch","IsMerging","IsRebasing",
                     "IsCherryPicking","StagedCount","ChangedCount","ConflictCount","AgentScannedAt","PersistedAt")
                VALUES ({linkId},1,'main',0,0,0,0,0,1,2,0,'2026-01-01T00:00:00','2026-01-01T00:00:00')
                """);
            for (var i = 0; i < 3; i++)
            {
                await Execute($"""
                    INSERT INTO "WorkspaceGitChangeEntries"
                        ("WorkspaceRepositoryId","Path","IndexChange","WorktreeChange","IsTracked","IsConflicted","IsSubmodule")
                    VALUES ({linkId},'src/f{i}.cs',{(i == 0 ? 1 : 0)},{(i == 0 ? 0 : 2)},1,0,0)
                    """);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file does not affect the result.
        }
    }
}
