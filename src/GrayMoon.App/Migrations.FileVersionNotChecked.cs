using System.Data;
using GrayMoon.App.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App;

public static partial class Migrations
{
    /// <summary>
    /// Strict step 22: adds the nullable <c>NotCheckedReason</c> column to <c>WorkspaceFileLineStatuses</c>. A row
    /// with a reason is a version-file token the check could not compare. Idempotent; existing rows are not
    /// rewritten. No try/catch: a failure reaches <see cref="RunStrictStepAsync"/>, which rolls back.
    /// </summary>
    public static async Task MigrateFileVersionNotCheckedReasonAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        if (!await TableExistsAsync(conn, "WorkspaceFileLineStatuses"))
            return;

        await AddNullableTextColumnIfMissingAsync(conn, "WorkspaceFileLineStatuses", "NotCheckedReason");
        logger?.LogDebug("WorkspaceFileLineStatuses.NotCheckedReason is present.");
    }
}
