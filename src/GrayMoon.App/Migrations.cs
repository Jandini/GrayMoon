using GrayMoon.App.Data;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App;

/// <summary>
/// Pre-release, the database schema is fully described by <see cref="AppDbContext.OnModelCreating"/> and
/// created via <c>EnsureCreated()</c> - there is no shipped version whose database needs patching, so no
/// schema-patching migrations live here. Once GrayMoon has a real release, any schema change made after that
/// point needs an idempotent <c>Migrate*Async</c> method here (guarded by <c>pragma_table_info</c>/<c>sqlite_master</c>
/// checks) to bring existing installed databases up to date - see CLAUDE.md "Database schema" for the pattern.
/// This class otherwise only runs one-time data seeding.
/// </summary>
public static class Migrations
{
    public static async Task RunAllAsync(AppDbContext dbContext)
    {
        await SeedDefaultWorkspaceRootPathAsync(dbContext);
        await MigrateWorkspaceRepositoriesHasSelfFileVersionTokenAsync(dbContext);
        await MigrateWorkspaceProjectsIsGeneratedAsync(dbContext);
        await MigrateDropGitHubApiUsageHourlyAsync(dbContext);
    }

    /// <summary>
    /// Adds the WorkspaceRepositories.HasSelfFileVersionToken column for local dev databases created before this
    /// column existed. EnsureCreated() only creates missing tables, not missing columns on tables that already
    /// exist, so an existing db/graymoon.db from an earlier build would otherwise throw "no such column" on any
    /// query against WorkspaceRepositories. Safe to keep even pre-release since it only ever adds a nullable
    /// column and is a no-op once the column exists.
    /// </summary>
    public static async Task MigrateWorkspaceRepositoriesHasSelfFileVersionTokenAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name = 'HasSelfFileVersionToken'";
            if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0)
                return;

            await using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN HasSelfFileVersionToken INTEGER NULL";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Table doesn't exist yet (fresh db, EnsureCreated will create it with the column already present).
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
    public static async Task MigrateWorkspaceProjectsIsGeneratedAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceProjects') WHERE name = 'IsGenerated'";
            if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0)
                return;

            await using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE WorkspaceProjects ADD COLUMN IsGenerated INTEGER NOT NULL DEFAULT 0";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Table doesn't exist yet (fresh db, EnsureCreated will create it with the column already present).
        }
    }

    /// <summary>
    /// Seeds the global workspace root path setting (Settings.WorkspaceRootPath) with C:\Workspace the first
    /// time the app runs against a fresh database - i.e. whenever no row for that key exists yet. Skipped once
    /// a row exists (including an explicitly cleared one), so it never overrides a user's own choice.
    /// </summary>
    public static async Task SeedDefaultWorkspaceRootPathAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
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
        catch
        {
            // Seed may already be applied or table doesn't exist yet
        }
    }

    /// <summary>
    /// Drops GitHubApiUsageHourly if a prior build created it via EnsureCreated. Usage counters are now
    /// in-memory only, so the table is unused. No-op when the table was never created.
    /// </summary>
    public static async Task MigrateDropGitHubApiUsageHourlyAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='GitHubApiUsageHourly'";
            if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) == 0)
                return;

            await using var dropCmd = conn.CreateCommand();
            dropCmd.CommandText = "DROP TABLE IF EXISTS GitHubApiUsageHourly";
            await dropCmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Table doesn't exist or already dropped.
        }
    }
}
