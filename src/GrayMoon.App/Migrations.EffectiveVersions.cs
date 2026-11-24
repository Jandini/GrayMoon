using System.Data;
using System.Data.Common;
using GrayMoon.App.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App;

public static partial class Migrations
{
    /// <summary>
    /// Strict step 21: effective versions. Adds the four nullable <c>EffectiveVersion*</c> columns to
    /// <c>WorkspaceRepositoryContextStates</c> and creates <c>WorkspaceRepositoryBranchBaselines</c> with its unique
    /// (context, repository, branch) index. Idempotent; existing rows are not rewritten. No try/catch: a failure
    /// reaches <see cref="RunStrictStepAsync"/>, which rolls back.
    /// </summary>
    public static async Task MigrateEffectiveVersionsAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await AddEffectiveVersionColumnsAsync(conn);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS "WorkspaceRepositoryBranchBaselines" (
                "WorkspaceRepositoryBranchBaselineId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceRepositoryBranchBaselines" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceFeatureContextId" INTEGER NOT NULL,
                "WorkspaceRepositoryId" INTEGER NOT NULL,
                "BranchName" TEXT NOT NULL,
                "ParentBranchName" TEXT NOT NULL,
                "BaseCommitSha" TEXT NOT NULL,
                "ResolvedStartRef" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                CONSTRAINT "FK_WorkspaceRepositoryBranchBaselines_WorkspaceFeatureContexts_WorkspaceFeatureContextId" FOREIGN KEY ("WorkspaceFeatureContextId") REFERENCES "WorkspaceFeatureContexts" ("WorkspaceFeatureContextId") ON DELETE CASCADE,
                CONSTRAINT "FK_WorkspaceRepositoryBranchBaselines_WorkspaceRepositories_WorkspaceRepositoryId" FOREIGN KEY ("WorkspaceRepositoryId") REFERENCES "WorkspaceRepositories" ("WorkspaceRepositoryId") ON DELETE CASCADE
            )
            """);
        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceRepositoryBranchBaselines_Context_Repo_Branch" ON "WorkspaceRepositoryBranchBaselines" ("WorkspaceFeatureContextId", "WorkspaceRepositoryId", "BranchName")
            """);
        await ExecuteNonQueryAsync(conn, """
            CREATE INDEX IF NOT EXISTS "IX_WorkspaceRepositoryBranchBaselines_WorkspaceRepositoryId" ON "WorkspaceRepositoryBranchBaselines" ("WorkspaceRepositoryId")
            """);

        logger?.LogDebug("Effective version columns and the branch baseline table are present.");
    }

    /// <summary>
    /// Adds the <c>EffectiveVersion*</c> columns when the context state table exists. Also run ahead of the strict steps
    /// (see <see cref="RunAllAsync(AppDbContext, ILogger?, TimeProvider?, Func{string, Task{bool}})"/>): older steps insert
    /// context state rows through EF, and the EF model already has these columns.
    /// </summary>
    internal static async Task AddEffectiveVersionColumnsAsync(DbConnection conn)
    {
        if (!await TableExistsAsync(conn, "WorkspaceRepositoryContextStates"))
            return;

        await AddNullableTextColumnIfMissingAsync(conn, "WorkspaceRepositoryContextStates", "EffectiveVersion");
        await AddNullableIntegerColumnIfMissingAsync(conn, "WorkspaceRepositoryContextStates", "EffectiveVersionKind");
        await AddNullableTextColumnIfMissingAsync(conn, "WorkspaceRepositoryContextStates", "EffectiveVersionCommitSha");
        await AddNullableTextColumnIfMissingAsync(conn, "WorkspaceRepositoryContextStates", "EffectiveVersionReasonCode");
    }
}
