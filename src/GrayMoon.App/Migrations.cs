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
}
