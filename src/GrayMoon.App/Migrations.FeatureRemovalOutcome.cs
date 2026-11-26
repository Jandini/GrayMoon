using System.Data;
using GrayMoon.App.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App;

public static partial class Migrations
{
    /// <summary>
    /// Strict step 23: adds the nullable <c>RemovalOutcomeJson</c> column to <c>WorkspaceFeatureRepositories</c>,
    /// where Remove Feature keeps what it did to a repository step by step, so an incomplete removal can be shown
    /// and continued after a restart. Idempotent; existing rows keep null (no removal ran, or it ran before this
    /// column existed, in which case the row's state and <c>LastError</c> still say where it stopped). No try/catch:
    /// a failure reaches <see cref="RunStrictStepAsync"/>, which rolls back.
    /// </summary>
    public static async Task MigrateFeatureRepositoryRemovalOutcomeAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        if (!await TableExistsAsync(conn, "WorkspaceFeatureRepositories"))
            return;

        await AddNullableTextColumnIfMissingAsync(conn, "WorkspaceFeatureRepositories", "RemovalOutcomeJson");
        logger?.LogDebug("WorkspaceFeatureRepositories.RemovalOutcomeJson is present.");
    }
}
