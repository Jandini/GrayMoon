using System.Data;
using System.Data.Common;
using GrayMoon.App.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App;

public static partial class Migrations
{
    /// <summary>
    /// Strict step 24: adds the nullable <c>EffectiveVersionEvidenceKey</c> column to
    /// <c>WorkspaceRepositoryContextStates</c>. Existing rows keep null, which no decision's key equals, so a stored
    /// branch point decision is checked once more and then stored with its key. Idempotent. No try/catch: a failure
    /// reaches <see cref="RunStrictStepAsync"/>, which rolls back.
    /// </summary>
    public static async Task MigrateEffectiveVersionEvidenceKeyAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await AddEffectiveVersionEvidenceKeyColumnAsync(conn);
        logger?.LogDebug("WorkspaceRepositoryContextStates.EffectiveVersionEvidenceKey is present.");
    }

    /// <summary>
    /// Adds the column when the context state table exists. Also run ahead of the strict steps: strict step 16 inserts
    /// context state rows through EF, and the EF model already has this column.
    /// </summary>
    internal static async Task AddEffectiveVersionEvidenceKeyColumnAsync(DbConnection conn)
    {
        if (!await TableExistsAsync(conn, "WorkspaceRepositoryContextStates"))
            return;

        await AddNullableTextColumnIfMissingAsync(conn, "WorkspaceRepositoryContextStates", "EffectiveVersionEvidenceKey");
    }
}
