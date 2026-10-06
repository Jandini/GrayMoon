using System.Data;
using System.Data.Common;
using GrayMoon.App.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App;

/// <summary>
/// Schema patches for databases created by an earlier build. EnsureCreated() only builds brand-new databases
/// from the current model; every change made after GrayMoon's first shipped release (0.1.0) needs a patch here
/// so existing installed databases come up to date too - see CLAUDE.md "Database schema" for the pattern.
///
/// Patches run in two modes, tracked with SQLite's <c>PRAGMA user_version</c>:
/// - Step 1 ("legacy baseline", <see cref="LegacyBaselineVersion"/>) bundles every patch written before this
///   versioning scheme existed. It runs in tolerant mode: an unexpected error in one patch is logged and
///   startup continues, because real installed databases have months of history this must not refuse to open.
/// - Step 2 and later (<see cref="StrictSteps"/>) are new, versioned patches. Each one runs in its own
///   transaction; a failure rolls back and throws <see cref="DatabaseMigrationException"/>, which stops startup.
/// A backup of the database file is written with <c>VACUUM INTO</c> before any pending step runs (step 1 or
/// later), so a failed strict step never leaves a user without a way back.
/// </summary>
public static partial class Migrations
{
    internal const int LegacyBaselineVersion = 1;

    /// <summary>
    /// Ordered strict (post-versioning) migration steps. Each unit that adds one appends a new entry here with
    /// the next free version number; never edit another unit's entry.
    /// </summary>
    internal static readonly IReadOnlyList<(int Version, string Name, Func<AppDbContext, Task> Action)> StrictSteps = new (int Version, string Name, Func<AppDbContext, Task> Action)[]
    {
        (2, "B2 orphan cleanup and WorkspaceProjects foreign key", dbContext => MigrateFeatureContextOrphanCleanupAndWorkspaceProjectsForeignKeyAsync(dbContext)),
        (3, "E1 case-insensitive Feature name index", dbContext => MigrateFeatureNameIndexCollationAsync(dbContext)),
        (4, "Workspace profile columns", dbContext => MigrateWorkspaceProfileColumnsAsync(dbContext)),
        (5, "Workspace repository role", dbContext => MigrateWorkspaceRepositoryRoleAsync(dbContext)),
    };

    public static async Task RunAllAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        var version = await GetUserVersionAsync(conn);
        var legacyPending = version < LegacyBaselineVersion;
        var strictPending = StrictSteps.Where(s => s.Version > version).OrderBy(s => s.Version).ToList();

        string? backupPath = null;
        if (legacyPending || strictPending.Count > 0)
            backupPath = await BackupDatabaseAsync(conn, logger);

        if (legacyPending)
        {
            await RunLegacyBaselineAsync(dbContext, logger);
            await SetUserVersionAsync(conn, LegacyBaselineVersion);
        }

        foreach (var step in strictPending)
            await RunStrictStepAsync(dbContext, step.Version, step.Name, step.Action, backupPath, logger);
    }

    private static async Task RunLegacyBaselineAsync(AppDbContext dbContext, ILogger logger)
    {
        await SeedDefaultWorkspaceRootPathAsync(dbContext, logger);
        await MigrateWorkspaceRepositoriesHasSelfFileVersionTokenAsync(dbContext, logger);
        await MigrateWorkspaceProjectsIsGeneratedAsync(dbContext, logger);
        await MigrateDropGitHubApiUsageHourlyAsync(dbContext, logger);
        await MigrateWorkspacesExcludeAiWorkflowsAsync(dbContext, logger);
        await MigrateWorkspaceGitRepositoryStatusLineStatsAsync(dbContext, logger);
        // Role must exist before any legacy step loads WorkspaceRepositoryLink through EF (strict step 5 repeats it idempotently).
        await MigrateWorkspaceRepositoryRoleAsync(dbContext, logger);
        await MigrateWorkspaceFeatureContextSchemaAsync(dbContext, logger);
    }

    /// <summary>
    /// Runs one strict-mode step in its own transaction. Rolls back and throws
    /// <see cref="DatabaseMigrationException"/> on any failure, so the schema is never left half-applied.
    /// Internal so unit tests can exercise failure handling directly, without needing a real entry in
    /// <see cref="StrictSteps"/>.
    /// </summary>
    internal static async Task RunStrictStepAsync(
        AppDbContext dbContext,
        int version,
        string name,
        Func<AppDbContext, Task> action,
        string? backupPath,
        ILogger logger)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            await action(dbContext);
            // Not ExecuteSqlRawAsync: SQLite's PRAGMA grammar does not accept a bound parameter for the
            // value, so this goes through a plain ADO.NET command (enlisted in the same transaction) instead
            // of an EF Core raw-SQL API. version is an internal int, never user input.
            var conn = dbContext.Database.GetDbConnection();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = transaction.GetDbTransaction();
                cmd.CommandText = $"PRAGMA user_version = {version};";
                await cmd.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex, "Migration step {Version} ({Name}) failed and was rolled back.", version, name);
            throw new DatabaseMigrationException(version, name, backupPath, ex);
        }
    }

    private static async Task<int> GetUserVersionAsync(DbConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = await cmd.ExecuteScalarAsync();
        return result is null ? 0 : Convert.ToInt32(result);
    }

    private static async Task SetUserVersionAsync(DbConnection conn, int version)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Backs up the database file with <c>VACUUM INTO</c> (a plain file copy of an open WAL database can be
    /// corrupt) before a pending migration step runs. Skipped for non-file-backed databases (in-memory tests).
    /// Keeps the 3 newest backups next to the database file and deletes older ones.
    /// </summary>
    private static async Task<string?> BackupDatabaseAsync(DbConnection conn, ILogger logger)
    {
        var dataSource = conn.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Skipping pre-migration database backup: database is not file-backed.");
            return null;
        }

        var directory = Path.GetDirectoryName(dataSource);
        if (string.IsNullOrEmpty(directory))
            directory = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(dataSource);
        var backupFileName = $"{fileName}.bak-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var backupPath = Path.Combine(directory, backupFileName);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "VACUUM INTO $backupPath;";
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "$backupPath";
            parameter.Value = backupPath;
            cmd.Parameters.Add(parameter);
            await cmd.ExecuteNonQueryAsync();
        }

        if (await IsBackupIntegrityOkAsync(backupPath))
            logger.LogInformation("Created pre-migration database backup at {BackupPath}.", backupPath);
        else
            logger.LogError("Pre-migration database backup at {BackupPath} failed integrity check.", backupPath);

        PruneOldBackups(directory, fileName, logger);

        return backupPath;
    }

    private static async Task<bool> IsBackupIntegrityOkAsync(string backupPath)
    {
        try
        {
            await using var connection = new SqliteConnection($"Data Source={backupPath}");
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = await cmd.ExecuteScalarAsync() as string;
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void PruneOldBackups(string directory, string dbFileName, ILogger logger)
    {
        try
        {
            var stale = Directory.GetFiles(directory, $"{dbFileName}.bak-*")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(3);
            foreach (var old in stale)
                File.Delete(old);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to prune old database backups in {Directory}.", directory);
        }
    }

    /// <summary>
    /// Adds the WorkspaceRepositories.HasSelfFileVersionToken column for local dev databases created before this
    /// column existed. EnsureCreated() only creates missing tables, not missing columns on tables that already
    /// exist, so an existing db/graymoon.db from an earlier build would otherwise throw "no such column" on any
    /// query against WorkspaceRepositories. Safe to keep even pre-release since it only ever adds a nullable
    /// column and is a no-op once the column exists.
    /// </summary>
    public static async Task MigrateWorkspaceRepositoriesHasSelfFileVersionTokenAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name = 'HasSelfFileVersionToken'";
            if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0)
                return;

            await using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN HasSelfFileVersionToken INTEGER NULL";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // Table doesn't exist yet (fresh db, EnsureCreated will create it with the column already present).
            logger.LogError(ex, "Legacy migration step MigrateWorkspaceRepositoriesHasSelfFileVersionTokenAsync failed; continuing startup.");
        }
    }

    /// <summary>
    /// Adds the WorkspaceProjects.IsGenerated column (virtual/generated NuGet package rows) for local dev databases
    /// created before this column existed. EnsureCreated() only creates missing tables, not missing columns on
    /// tables that already exist, so an existing db/graymoon.db from an earlier build would otherwise throw
    /// "no such column: w.IsGenerated" on any query that reads WorkspaceProjects (including workspace sync).
    /// Safe to keep even pre-release since it only ever adds a column with a default value and is a no-op once
    /// the column exists.
    /// </summary>
    public static async Task MigrateWorkspaceProjectsIsGeneratedAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceProjects') WHERE name = 'IsGenerated'";
            if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0)
                return;

            await using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE WorkspaceProjects ADD COLUMN IsGenerated INTEGER NOT NULL DEFAULT 0";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // Table doesn't exist yet (fresh db, EnsureCreated will create it with the column already present).
            logger.LogError(ex, "Legacy migration step MigrateWorkspaceProjectsIsGeneratedAsync failed; continuing startup.");
        }
    }

    /// <summary>
    /// Seeds the global workspace root path setting (Settings.WorkspaceRootPath) with C:\Workspace the first
    /// time the app runs against a fresh database - i.e. whenever no row for that key exists yet. Skipped once
    /// a row exists (including an explicitly cleared one), so it never overrides a user's own choice.
    /// </summary>
    public static async Task SeedDefaultWorkspaceRootPathAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Settings'";
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                return;

            cmd.CommandText = "SELECT COUNT(*) FROM Settings WHERE Key = @key";
            var keyParam = cmd.CreateParameter();
            keyParam.ParameterName = "@key";
            keyParam.Value = Repositories.AppSettingRepository.WorkspaceRootPathKey;
            cmd.Parameters.Add(keyParam);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0)
                return;

            cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES (@key, @value)";
            var valueParam = cmd.CreateParameter();
            valueParam.ParameterName = "@value";
            valueParam.Value = @"C:\Workspace";
            cmd.Parameters.Add(valueParam);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // Seed may already be applied or table doesn't exist yet
            logger.LogError(ex, "Legacy migration step SeedDefaultWorkspaceRootPathAsync failed; continuing startup.");
        }
    }

    /// <summary>
    /// Drops GitHubApiUsageHourly if a prior build created it via EnsureCreated. Usage counters are now
    /// in-memory only, so the table is unused. No-op when the table was never created.
    /// </summary>
    public static async Task MigrateDropGitHubApiUsageHourlyAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='GitHubApiUsageHourly'";
            if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) == 0)
                return;

            await using var dropCmd = conn.CreateCommand();
            dropCmd.CommandText = "DROP TABLE IF EXISTS GitHubApiUsageHourly";
            await dropCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // Table doesn't exist or already dropped.
            logger.LogError(ex, "Legacy migration step MigrateDropGitHubApiUsageHourlyAsync failed; continuing startup.");
        }
    }

    /// <summary>
    /// Adds the Workspaces.ExcludeAiWorkflows column for local dev databases created before this column existed.
    /// EnsureCreated() only creates missing tables, not missing columns on tables that already exist, so an
    /// existing db/graymoon.db from an earlier build would otherwise throw "no such column" on any query against
    /// Workspaces. Safe to keep even pre-release since it only ever adds a column with a default value and is a
    /// no-op once the column exists.
    /// </summary>
    public static async Task MigrateWorkspacesExcludeAiWorkflowsAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Workspaces') WHERE name = 'ExcludeAiWorkflows'";
            if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0)
                return;

            await using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE Workspaces ADD COLUMN ExcludeAiWorkflows INTEGER NOT NULL DEFAULT 1";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // Table doesn't exist yet (fresh db, EnsureCreated will create it with the column already present).
            logger.LogError(ex, "Legacy migration step MigrateWorkspacesExcludeAiWorkflowsAsync failed; continuing startup.");
        }
    }

    /// <summary>
    /// Adds WorkspaceGitRepositoryStatus.Insertions and Deletions for local databases created before
    /// header line stats existed. EnsureCreated() only creates missing tables, not missing columns.
    /// </summary>
    public static async Task MigrateWorkspaceGitRepositoryStatusLineStatsAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await AddNullableIntegerColumnIfMissingAsync(conn, "WorkspaceGitRepositoryStatus", "Insertions");
            await AddNullableIntegerColumnIfMissingAsync(conn, "WorkspaceGitRepositoryStatus", "Deletions");
            await AddNullableIntegerColumnIfMissingAsync(conn, "WorkspaceGitRepositoryStatus", "StagedInsertions");
            await AddNullableIntegerColumnIfMissingAsync(conn, "WorkspaceGitRepositoryStatus", "StagedDeletions");
        }
        catch (Exception ex)
        {
            // Table doesn't exist yet (fresh db, EnsureCreated will create it with the columns already present).
            logger.LogError(ex, "Legacy migration step MigrateWorkspaceGitRepositoryStatusLineStatsAsync failed; continuing startup.");
        }
    }

    /// <summary>
    /// Adds the three Workspaces profile columns (Type, VersioningMode, CiProvider) and, on the one run that
    /// creates them, backfills every existing row to the .NET triple
    /// (DotNetDependency / GitVersion / GitHubActions).
    ///
    /// The backfill is a compatibility requirement, not a convenience: every Workspace that exists at the
    /// moment the columns are created predates workspace profiles, so it must keep behaving as a .NET
    /// Dependency + GitVersion + GitHub Actions Workspace with no user action. It therefore runs only on the
    /// branch that just created the columns. Once the columns exist their values are the user's own choices,
    /// and an unconditional UPDATE would stomp a Workspace deliberately switched to Basic on the next startup.
    ///
    /// On a brand-new database the columns already exist, because EnsureCreated() built them from the model
    /// with the Basic/None/None model defaults; nothing is added and the backfill correctly does not run - and
    /// even if it did, there are no rows yet.
    ///
    /// Workspace type is never inferred from whether projects currently exist: a .NET Workspace may legitimately
    /// have none right now.
    ///
    /// This is strict step 4, so it deliberately has no try/catch - a failure must propagate to
    /// <see cref="RunStrictStepAsync"/> so the step rolls back and startup stops.
    /// </summary>
    public static async Task MigrateWorkspaceProfileColumnsAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        var addedAny = false;
        foreach (var columnName in new[] { "Type", "VersioningMode", "CiProvider" })
            addedAny |= await AddWorkspaceProfileColumnIfMissingAsync(conn, columnName);

        if (!addedAny)
            return;

        await using var backfillCmd = conn.CreateCommand();
        backfillCmd.CommandText = "UPDATE Workspaces SET Type = 1, VersioningMode = 1, CiProvider = 1";
        var backfilled = await backfillCmd.ExecuteNonQueryAsync();

        logger.LogInformation(
            "Backfilled {RowCount} pre-profile Workspace row(s) to DotNetDependency / GitVersion / GitHubActions.",
            backfilled);
    }

    /// <summary>
    /// Adds WorkspaceRepositories.Role (0 = Source, 1 = Workspace) and the filtered unique index that allows at
    /// most one Workspace-role link per Workspace. Strict step 5: no try/catch, a failure propagates to
    /// <see cref="RunStrictStepAsync"/>.
    /// </summary>
    public static async Task MigrateWorkspaceRepositoryRoleAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name = 'Role'";
            if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) == 0)
            {
                await using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN Role INTEGER NOT NULL DEFAULT 0";
                await alterCmd.ExecuteNonQueryAsync();
            }
        }

        await using var indexCmd = conn.CreateCommand();
        indexCmd.CommandText =
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_WorkspaceRepositories_WorkspaceId_WorkspaceRole " +
            "ON WorkspaceRepositories(WorkspaceId) WHERE Role = 1";
        await indexCmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Adds one Workspaces profile column when it is absent. Returns true only when the column was actually
    /// created, which is what makes the profile backfill run exactly once.
    /// </summary>
    private static async Task<bool> AddWorkspaceProfileColumnIfMissingAsync(DbConnection conn, string columnName)
    {
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('Workspaces') WHERE name = '{columnName}'";
        if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0)
            return false;

        await using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE Workspaces ADD COLUMN {columnName} INTEGER NOT NULL DEFAULT 0";
        await alterCmd.ExecuteNonQueryAsync();
        return true;
    }

    private static async Task AddNullableIntegerColumnIfMissingAsync(System.Data.Common.DbConnection conn, string tableName, string columnName)
    {
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = '{columnName}'";
        if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0)
            return;

        await using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} INTEGER NULL";
        await alterCmd.ExecuteNonQueryAsync();
    }
}
