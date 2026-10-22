using System.Data;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App;

public static partial class Migrations
{
    /// <summary>
    /// Replaces name identity of WorkspaceProjects with <c>ProjectIdentityKey</c>. Strict step 11. Idempotent.
    /// Adds the column, backfills it from each row's own path (physical) or package id (generated) without touching
    /// ProjectId or any foreign key, then swaps the unique (context, repo, name) index for a non-unique one plus a
    /// unique identity index. A row whose key would repeat an earlier row's key (or cannot be derived) keeps a NULL
    /// key, which the unique index ignores; it is logged and left for the next scan to reconcile - never deleted.
    /// </summary>
    public static async Task MigrateWorkspaceProjectIdentityKeyAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        // Quoted exactly like EF's CREATE TABLE so the stored table definition matches a fresh database.
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceProjects') WHERE name = 'ProjectIdentityKey'";
            if (Convert.ToInt32(await check.ExecuteScalarAsync()) == 0)
            {
                await using var alter = conn.CreateCommand();
                alter.CommandText = """ALTER TABLE "WorkspaceProjects" ADD COLUMN "ProjectIdentityKey" TEXT NULL""";
                await alter.ExecuteNonQueryAsync();
            }
        }

        var rows = new List<(long ProjectId, long ContextId, long RepositoryId, bool IsGenerated, string? Path, string? PackageId, string? Key)>();
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = """
                SELECT "ProjectId", "WorkspaceFeatureContextId", "RepositoryId", "IsGenerated", "ProjectFilePath", "PackageId", "ProjectIdentityKey"
                FROM "WorkspaceProjects"
                WHERE "WorkspaceFeatureContextId" IS NOT NULL
                ORDER BY "ProjectId"
                """;
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetBoolean(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }

        // Keys already stored (a re-run, or EnsureCreated plus writes) win over new assignments.
        var taken = new HashSet<(long, long, bool, string)>(
            rows.Where(r => r.Key is not null).Select(r => (r.ContextId, r.RepositoryId, r.IsGenerated, r.Key!)));
        var assigned = 0;
        var skipped = 0;
        foreach (var r in rows.Where(r => r.Key is null))
        {
            var key = r.IsGenerated ? ProjectIdentity.GeneratedKey(r.PackageId) : ProjectIdentity.PhysicalKey(r.Path);
            if (key is null || !taken.Add((r.ContextId, r.RepositoryId, r.IsGenerated, key)))
            {
                skipped++;
                logger?.LogWarning(
                    "WorkspaceProjects identity backfill: ProjectId={ProjectId} ContextId={ContextId} RepositoryId={RepositoryId} IsGenerated={IsGenerated} Path={Path} PackageId={PackageId} has no usable or unique identity key; left NULL until the next scan.",
                    r.ProjectId, r.ContextId, r.RepositoryId, r.IsGenerated, r.Path, r.PackageId);
                continue;
            }

            await using var update = conn.CreateCommand();
            update.CommandText = """UPDATE "WorkspaceProjects" SET "ProjectIdentityKey" = $key WHERE "ProjectId" = $id""";
            var keyParam = update.CreateParameter();
            keyParam.ParameterName = "$key";
            keyParam.Value = key;
            update.Parameters.Add(keyParam);
            var idParam = update.CreateParameter();
            idParam.ParameterName = "$id";
            idParam.Value = r.ProjectId;
            update.Parameters.Add(idParam);
            await update.ExecuteNonQueryAsync();
            assigned++;
        }

        logger?.LogInformation("WorkspaceProjects identity backfill: Assigned={Assigned} LeftNull={Skipped}", assigned, skipped);

        foreach (var sql in new[]
        {
            """DROP INDEX IF EXISTS "IX_WorkspaceProjects_Context_Repo_Name" """,
            """CREATE INDEX IF NOT EXISTS "IX_WorkspaceProjects_Context_Repo_Name" ON "WorkspaceProjects" ("WorkspaceFeatureContextId", "RepositoryId", "ProjectName")""",
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceProjects_Context_Repo_Identity"
            ON "WorkspaceProjects" ("WorkspaceFeatureContextId", "RepositoryId", "IsGenerated", "ProjectIdentityKey")
            WHERE "ProjectIdentityKey" IS NOT NULL AND "WorkspaceFeatureContextId" IS NOT NULL
            """,
        })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
