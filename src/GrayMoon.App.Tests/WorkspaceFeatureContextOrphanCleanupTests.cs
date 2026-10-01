using System.Data.Common;
using System.Text;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// B2: orphan rows left behind by removed Features are deleted, and WorkspaceProjects (only) is rebuilt with
/// the WorkspaceFeatureContextId foreign key a fresh EnsureCreated() database has. WorkspaceFileLineStatuses
/// gets orphan cleanup only; a fresh database has no foreign key on that column either (no navigation
/// property, no Fluent API configuration for it in AppDbContext), so adding one would break schema parity
/// instead of restoring it.
/// </summary>
public sealed class WorkspaceFeatureContextOrphanCleanupTests
{
    [Fact]
    public async Task Orphan_rows_are_deleted_but_null_and_valid_context_rows_survive_with_their_ids()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateUpgradedDatabaseWithoutWorkspaceProjectsForeignKeyAsync(connection);

        var validContextId = await InsertFeatureContextAsync(connection);
        const int orphanContextId = 999999;

        var validProjectId = await InsertProjectAsync(connection, validContextId, "ValidProject");
        var nullContextProjectId = await InsertProjectAsync(connection, contextId: null, "NullContextProject");
        var orphanProjectId = await InsertProjectAsync(connection, orphanContextId, "OrphanProject");
        var orphanProjectId2 = await InsertProjectAsync(connection, orphanContextId, "OrphanProject2");

        var validStatusId = await InsertFileLineStatusAsync(connection, validContextId, "valid.txt");
        var nullContextStatusId = await InsertFileLineStatusAsync(connection, contextId: null, "null-context.txt");
        var orphanStatusId = await InsertFileLineStatusAsync(connection, orphanContextId, "orphan.txt");

        var validDependencyId = await InsertProjectDependencyAsync(connection, validProjectId, nullContextProjectId);
        var orphanDependencyId = await InsertProjectDependencyAsync(connection, orphanProjectId, orphanProjectId2);

        await Migrations.MigrateFeatureContextOrphanCleanupAndWorkspaceProjectsForeignKeyAsync(db);

        var remainingProjectIds = await QueryIntColumnAsync(connection, "SELECT \"ProjectId\" FROM \"WorkspaceProjects\" ORDER BY \"ProjectId\"");
        Assert.Equal(new[] { validProjectId, nullContextProjectId }, remainingProjectIds);

        var remainingStatusIds = await QueryIntColumnAsync(connection, "SELECT \"StatusId\" FROM \"WorkspaceFileLineStatuses\" ORDER BY \"StatusId\"");
        Assert.Equal(new[] { validStatusId, nullContextStatusId }, remainingStatusIds);

        var remainingDependencyIds = await QueryIntColumnAsync(connection, "SELECT \"ProjectDependencyId\" FROM \"ProjectDependencies\" ORDER BY \"ProjectDependencyId\"");
        Assert.Equal(new[] { validDependencyId }, remainingDependencyIds);
        Assert.DoesNotContain(orphanDependencyId, remainingDependencyIds);
        Assert.DoesNotContain(orphanProjectId, remainingProjectIds);
        Assert.DoesNotContain(orphanStatusId, remainingStatusIds);
    }

    [Fact]
    public async Task Rebuild_adds_the_WorkspaceFeatureContextId_foreign_key_to_WorkspaceProjects_and_preserves_row_ids()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateUpgradedDatabaseWithoutWorkspaceProjectsForeignKeyAsync(connection);

        Assert.Empty(await QueryForeignKeyTargetTablesAsync(connection, "WorkspaceProjects", onlyTable: "WorkspaceFeatureContexts"));

        var contextId = await InsertFeatureContextAsync(connection);
        var projectAId = await InsertProjectAsync(connection, contextId, "ProjA");
        var projectBId = await InsertProjectAsync(connection, contextId, "ProjB");
        var dependencyId = await InsertProjectDependencyAsync(connection, projectAId, projectBId);

        await Migrations.MigrateFeatureContextOrphanCleanupAndWorkspaceProjectsForeignKeyAsync(db);

        var fkTargets = await QueryForeignKeyTargetTablesAsync(connection, "WorkspaceProjects", onlyTable: "WorkspaceFeatureContexts");
        Assert.Single(fkTargets);

        var remainingProjectIds = await QueryIntColumnAsync(connection, "SELECT \"ProjectId\" FROM \"WorkspaceProjects\" ORDER BY \"ProjectId\"");
        Assert.Equal(new[] { projectAId, projectBId }, remainingProjectIds);

        var remainingDependencyIds = await QueryIntColumnAsync(connection, "SELECT \"ProjectDependencyId\" FROM \"ProjectDependencies\"");
        Assert.Equal(new[] { dependencyId }, remainingDependencyIds);

        // The original indexes must still exist (recreated after the rebuild), not just the foreign key.
        var indexNames = await QueryTextColumnAsync(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'WorkspaceProjects' ORDER BY name");
        Assert.Contains("IX_WorkspaceProjects_Context_Repo_Name", indexNames);
        Assert.Contains("IX_WorkspaceProjects_MatchedConnectorId", indexNames);
        Assert.Contains("IX_WorkspaceProjects_RepositoryId", indexNames);
        Assert.Contains("IX_WorkspaceProjects_WorkspaceId_ProjectName_ProjectId", indexNames);
        Assert.Contains("IX_WorkspaceProjects_Workspace_Repo_Name", indexNames);

        var violationCount = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check('WorkspaceProjects')");
        Assert.Equal(0, violationCount);
    }

    [Fact]
    public async Task WorkspaceFileLineStatuses_keeps_no_foreign_key_matching_a_fresh_database()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateUpgradedDatabaseWithoutWorkspaceProjectsForeignKeyAsync(connection);

        var contextId = await InsertFeatureContextAsync(connection);
        await InsertFileLineStatusAsync(connection, contextId, "kept.txt");

        await Migrations.MigrateFeatureContextOrphanCleanupAndWorkspaceProjectsForeignKeyAsync(db);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_list('WorkspaceFileLineStatuses')";
        var fkCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(0, fkCount);
    }

    [Fact]
    public async Task Running_the_step_twice_is_a_no_op_the_second_time()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateUpgradedDatabaseWithoutWorkspaceProjectsForeignKeyAsync(connection);

        var contextId = await InsertFeatureContextAsync(connection);
        var projectId = await InsertProjectAsync(connection, contextId, "SoloProject");

        await Migrations.MigrateFeatureContextOrphanCleanupAndWorkspaceProjectsForeignKeyAsync(db);
        await Migrations.MigrateFeatureContextOrphanCleanupAndWorkspaceProjectsForeignKeyAsync(db);

        var remainingProjectIds = await QueryIntColumnAsync(connection, "SELECT \"ProjectId\" FROM \"WorkspaceProjects\"");
        Assert.Equal(new[] { projectId }, remainingProjectIds);

        var fkTargets = await QueryForeignKeyTargetTablesAsync(connection, "WorkspaceProjects", onlyTable: "WorkspaceFeatureContexts");
        Assert.Single(fkTargets);
    }

    [Fact]
    public async Task Schema_parity_fresh_database_and_upgraded_010_fixture_match_after_the_step()
    {
        await using var freshConnection = new SqliteConnection("Data Source=:memory:");
        await freshConnection.OpenAsync();
        var freshOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(freshConnection).Options;
        await using var freshDb = new AppDbContext(freshOptions);
        await freshDb.Database.EnsureCreatedAsync();

        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "graymoon-0.1.0.db");
        var workingCopyPath = Path.Combine(Path.GetTempPath(), $"schema-parity-{Guid.NewGuid():N}.db");
        File.Copy(fixturePath, workingCopyPath, overwrite: true);

        try
        {
            var upgradedOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={workingCopyPath}").Options;
            await using var upgradedDb = new AppDbContext(upgradedOptions);
            await Migrations.RunAllAsync(upgradedDb);

            // WorkspaceProjects is rebuilt by the step, so its full table definition (column order and all)
            // matches a fresh database exactly, not only its foreign keys.
            var freshProjectsSql = await ReadTableDefinitionAsync(freshConnection, "WorkspaceProjects");
            var upgradedProjectsSql = await ReadTableDefinitionAsync(upgradedDb.Database.GetDbConnection(), "WorkspaceProjects");
            Assert.Equal(Normalize(freshProjectsSql), Normalize(upgradedProjectsSql));

            // WorkspaceFileLineStatuses keeps its column order from the older ALTER TABLE-based upgrade (out of
            // scope for this unit); what must match a fresh database is the absence of a foreign key on
            // WorkspaceFeatureContextId, which is checked below for both tables.
            foreach (var table in new[] { "WorkspaceProjects", "WorkspaceFileLineStatuses" })
            {
                var freshForeignKeys = await ReadForeignKeyListAsync(freshConnection, table);
                var upgradedForeignKeys = await ReadForeignKeyListAsync(upgradedDb.Database.GetDbConnection(), table);
                Assert.Equal(freshForeignKeys, upgradedForeignKeys);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(workingCopyPath))
                File.Delete(workingCopyPath);
        }
    }

    [Fact]
    public async Task Dependency_graph_on_the_010_fixture_is_unchanged_after_the_step()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "graymoon-0.1.0.db");

        var beforeProjects = new List<(int ProjectId, string ProjectName)>();
        var beforeDependencies = new List<(int DependentProjectId, int ReferencedProjectId)>();
        await using (var rawConnection = new SqliteConnection($"Data Source={fixturePath};Mode=ReadOnly"))
        {
            await rawConnection.OpenAsync();
            await using var cmd = rawConnection.CreateCommand();
            cmd.CommandText = "SELECT \"ProjectId\", \"ProjectName\" FROM \"WorkspaceProjects\" ORDER BY \"ProjectId\"";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                beforeProjects.Add((reader.GetInt32(0), reader.GetString(1)));

            await using var depCmd = rawConnection.CreateCommand();
            depCmd.CommandText = "SELECT \"DependentProjectId\", \"ReferencedProjectId\" FROM \"ProjectDependencies\" ORDER BY \"ProjectDependencyId\"";
            await using var depReader = await depCmd.ExecuteReaderAsync();
            while (await depReader.ReadAsync())
                beforeDependencies.Add((depReader.GetInt32(0), depReader.GetInt32(1)));
        }

        Assert.NotEmpty(beforeProjects);
        Assert.NotEmpty(beforeDependencies);

        var workingCopyPath = Path.Combine(Path.GetTempPath(), $"dependency-graph-{Guid.NewGuid():N}.db");
        File.Copy(fixturePath, workingCopyPath, overwrite: true);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={workingCopyPath}").Options;
            await using var db = new AppDbContext(options);
            await Migrations.RunAllAsync(db);

            var afterProjects = await db.WorkspaceProjects.AsNoTracking()
                .OrderBy(p => p.ProjectId)
                .Select(p => new { p.ProjectId, p.ProjectName })
                .ToListAsync();
            var afterDependencies = await db.ProjectDependencies.AsNoTracking()
                .OrderBy(d => d.ProjectDependencyId)
                .Select(d => new { d.DependentProjectId, d.ReferencedProjectId })
                .ToListAsync();

            Assert.Equal(beforeProjects, afterProjects.Select(p => (p.ProjectId, p.ProjectName)).ToList());
            Assert.Equal(beforeDependencies, afterDependencies.Select(d => (d.DependentProjectId, d.ReferencedProjectId)).ToList());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(workingCopyPath))
                File.Delete(workingCopyPath);
        }
    }

    /// <summary>
    /// Builds an in-memory database shaped like a real upgraded install before B2: full fresh schema from
    /// EnsureCreated(), then WorkspaceProjects is rebuilt without its WorkspaceFeatureContextId foreign key
    /// (the one constraint that an ALTER TABLE-based upgrade cannot add), matching what
    /// EnsureProjectAndFileLineContextColumnsAsync actually produces on a real installed database.
    /// </summary>
    private static async Task<AppDbContext> CreateUpgradedDatabaseWithoutWorkspaceProjectsForeignKeyAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var indexDefinitions = new List<string>();
        await using (var indexCmd = connection.CreateCommand())
        {
            indexCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'WorkspaceProjects' AND sql IS NOT NULL";
            await using var reader = await indexCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                indexDefinitions.Add(reader.GetString(0));
        }

        await ExecuteAsync(connection, """
            CREATE TABLE "WorkspaceProjects_old" (
                "ProjectId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceProjects" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceId" INTEGER NOT NULL,
                "RepositoryId" INTEGER NOT NULL,
                "ProjectName" TEXT NOT NULL,
                "ProjectType" INTEGER NOT NULL,
                "ProjectFilePath" TEXT NOT NULL,
                "TargetFramework" TEXT NOT NULL,
                "PackageId" TEXT NULL,
                "IsGenerated" INTEGER NOT NULL,
                "MatchedConnectorId" INTEGER NULL,
                "WorkspaceFeatureContextId" INTEGER NULL,
                CONSTRAINT "FK_WorkspaceProjects_Connectors_MatchedConnectorId" FOREIGN KEY ("MatchedConnectorId") REFERENCES "Connectors" ("ConnectorId") ON DELETE SET NULL,
                CONSTRAINT "FK_WorkspaceProjects_Repositories_RepositoryId" FOREIGN KEY ("RepositoryId") REFERENCES "Repositories" ("RepositoryId") ON DELETE CASCADE,
                CONSTRAINT "FK_WorkspaceProjects_Workspaces_WorkspaceId" FOREIGN KEY ("WorkspaceId") REFERENCES "Workspaces" ("WorkspaceId") ON DELETE CASCADE
            );
            """);
        await ExecuteAsync(connection, """
            INSERT INTO "WorkspaceProjects_old"
                ("ProjectId", "WorkspaceId", "RepositoryId", "ProjectName", "ProjectType", "ProjectFilePath",
                 "TargetFramework", "PackageId", "IsGenerated", "MatchedConnectorId", "WorkspaceFeatureContextId")
            SELECT "ProjectId", "WorkspaceId", "RepositoryId", "ProjectName", "ProjectType", "ProjectFilePath",
                   "TargetFramework", "PackageId", "IsGenerated", "MatchedConnectorId", "WorkspaceFeatureContextId"
            FROM "WorkspaceProjects";
            """);
        await ExecuteAsync(connection, """DROP TABLE "WorkspaceProjects";""");
        await ExecuteAsync(connection, """ALTER TABLE "WorkspaceProjects_old" RENAME TO "WorkspaceProjects";""");

        foreach (var indexSql in indexDefinitions)
            await ExecuteAsync(connection, indexSql);

        return db;
    }

    private static async Task<int> InsertFeatureContextAsync(SqliteConnection connection)
    {
        var workspaceId = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM \"Workspaces\"") == 0
            ? await InsertWorkspaceAsync(connection)
            : await ScalarIntAsync(connection, "SELECT \"WorkspaceId\" FROM \"Workspaces\" LIMIT 1");

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "WorkspaceFeatureContexts" ("WorkspaceId", "Kind", "WorkspaceFeatureId", "CreatedAt", "LastSyncedAt", "IsInSync")
            VALUES ($workspaceId, 1, NULL, $createdAt, NULL, 1);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$workspaceId", workspaceId);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int> InsertWorkspaceAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "Workspaces" ("Name", "IsDefault", "IsInSync") VALUES ('test-workspace', 0, 0);
            SELECT last_insert_rowid();
            """;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int> InsertProjectAsync(SqliteConnection connection, int? contextId, string projectName)
    {
        var workspaceId = await ScalarIntAsync(connection, "SELECT \"WorkspaceId\" FROM \"Workspaces\" LIMIT 1");
        var repositoryId = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM \"Repositories\"") == 0
            ? await InsertRepositoryAsync(connection)
            : await ScalarIntAsync(connection, "SELECT \"RepositoryId\" FROM \"Repositories\" LIMIT 1");

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "WorkspaceProjects"
                ("WorkspaceId", "WorkspaceFeatureContextId", "RepositoryId", "ProjectName", "ProjectType",
                 "ProjectFilePath", "TargetFramework", "IsGenerated")
            VALUES ($workspaceId, $contextId, $repositoryId, $projectName, 0, $path, 'net9.0', 0);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$workspaceId", workspaceId);
        cmd.Parameters.AddWithValue("$contextId", (object?)contextId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repositoryId", repositoryId);
        cmd.Parameters.AddWithValue("$projectName", projectName);
        cmd.Parameters.AddWithValue("$path", $@"src\{projectName}\{projectName}.csproj");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int> InsertRepositoryAsync(SqliteConnection connection)
    {
        var connectorId = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM \"Connectors\"") == 0
            ? await InsertConnectorAsync(connection)
            : await ScalarIntAsync(connection, "SELECT \"ConnectorId\" FROM \"Connectors\" LIMIT 1");

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "Repositories" ("ConnectorId", "RepositoryName", "Visibility", "CloneUrl")
            VALUES ($connectorId, 'test-repo', 'Public', 'https://example.invalid/test-repo.git');
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$connectorId", connectorId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int> InsertConnectorAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "Connectors" ("ConnectorName", "ConnectorType", "ApiBaseUrl", "Status", "IsHealthy")
            VALUES ('test-connector', 0, 'https://api.github.com/', 'Unknown', 0);
            SELECT last_insert_rowid();
            """;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int> InsertFileLineStatusAsync(SqliteConnection connection, int? contextId, string filePath)
    {
        var workspaceId = await ScalarIntAsync(connection, "SELECT \"WorkspaceId\" FROM \"Workspaces\" LIMIT 1");
        var repositoryId = await ScalarIntAsync(connection, "SELECT \"RepositoryId\" FROM \"Repositories\" LIMIT 1");

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "WorkspaceFileLineStatuses"
                ("WorkspaceId", "WorkspaceFeatureContextId", "RepositoryId", "FilePath", "FileName", "TokenName",
                 "TotalMatchedLines", "OutOfDateLines")
            VALUES ($workspaceId, $contextId, $repositoryId, $filePath, $filePath, 'version', 1, 0);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$workspaceId", workspaceId);
        cmd.Parameters.AddWithValue("$contextId", (object?)contextId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repositoryId", repositoryId);
        cmd.Parameters.AddWithValue("$filePath", filePath);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int> InsertProjectDependencyAsync(SqliteConnection connection, int dependentProjectId, int referencedProjectId)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "ProjectDependencies" ("DependentProjectId", "ReferencedProjectId")
            VALUES ($dependentProjectId, $referencedProjectId);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$dependentProjectId", dependentProjectId);
        cmd.Parameters.AddWithValue("$referencedProjectId", referencedProjectId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int> ScalarIntAsync(DbConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is null || result is DBNull ? 0 : Convert.ToInt32(result);
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<int>> QueryIntColumnAsync(DbConnection connection, string sql)
    {
        var values = new List<int>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetInt32(0));
        return values;
    }

    private static async Task<List<string>> QueryTextColumnAsync(DbConnection connection, string sql)
    {
        var values = new List<string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<List<string>> QueryForeignKeyTargetTablesAsync(DbConnection connection, string tableName, string onlyTable)
    {
        var values = new List<string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT \"table\" FROM pragma_foreign_key_list('{tableName}') WHERE \"table\" = '{onlyTable}'";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<string> ReadTableDefinitionAsync(DbConnection connection, string tableName)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '{tableName}'";
        var result = await cmd.ExecuteScalarAsync();
        return result as string ?? string.Empty;
    }

    private static async Task<List<string>> ReadForeignKeyListAsync(DbConnection connection, string tableName)
    {
        var rows = new List<string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT \"table\", \"from\", \"to\", \"on_delete\" FROM pragma_foreign_key_list('{tableName}') ORDER BY \"table\", \"from\"";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add($"{reader.GetString(0)}:{reader.GetString(1)}:{reader.GetString(2)}:{reader.GetString(3)}");
        return rows;
    }

    private static string Normalize(string sql) =>
        string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
