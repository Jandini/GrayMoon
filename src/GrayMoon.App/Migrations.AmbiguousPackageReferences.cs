using System.Data;
using GrayMoon.App.Data;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App;

public static partial class Migrations
{
    /// <summary>
    /// Stores PackageReferences whose package id has more than one producer, for the Packages "Used by" list.
    /// Strict step 12. Idempotent. Does not add project-dependency edges.
    /// </summary>
    public static async Task MigrateAmbiguousPackageReferencesAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS "AmbiguousPackageReferences" (
                    "AmbiguousPackageReferenceId" INTEGER NOT NULL CONSTRAINT "PK_AmbiguousPackageReferences" PRIMARY KEY AUTOINCREMENT,
                    "DependentProjectId" INTEGER NOT NULL,
                    "PackageId" TEXT NOT NULL,
                    "Version" TEXT NULL,
                    CONSTRAINT "FK_AmbiguousPackageReferences_WorkspaceProjects_DependentProjectId" FOREIGN KEY ("DependentProjectId") REFERENCES "WorkspaceProjects" ("ProjectId") ON DELETE CASCADE
                );
                """;
            await create.ExecuteNonQueryAsync();
        }

        await using var index = conn.CreateCommand();
        index.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AmbiguousPackageReferences_DependentProjectId_PackageId"
            ON "AmbiguousPackageReferences" ("DependentProjectId", "PackageId");
            """;
        await index.ExecuteNonQueryAsync();
        logger?.LogDebug("AmbiguousPackageReferences table is present.");
    }
}
