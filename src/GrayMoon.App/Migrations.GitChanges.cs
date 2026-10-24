using System.Data;
using GrayMoon.App.Data;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App;

public static partial class Migrations
{
    /// <summary>
    /// Path indexes for Git Changes Layer B prefix/search queries. Strict step 13. Idempotent.
    /// Replaces the (context, repo) index whose prefix is covered by the new (context, repo, Path) index.
    /// </summary>
    public static async Task MigrateGitChangesPathIndexesAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await using (var drop = conn.CreateCommand())
        {
            drop.CommandText = """DROP INDEX IF EXISTS "IX_WorkspaceGitContextChangeEntries_Context_Repo";""";
            await drop.ExecuteNonQueryAsync();
        }

        foreach (var sql in new[]
        {
            """
            CREATE INDEX IF NOT EXISTS "IX_WorkspaceGitContextChangeEntries_Context_Repo_Path"
            ON "WorkspaceGitContextChangeEntries" ("WorkspaceFeatureContextId", "WorkspaceRepositoryId", "Path")
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_WorkspaceGitContextChangeEntries_Context_Path"
            ON "WorkspaceGitContextChangeEntries" ("WorkspaceFeatureContextId", "Path")
            """,
        })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        logger?.LogDebug("Git Changes path indexes are present.");
    }
}
