using System.Data;
using GrayMoon.App.Data;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App;

public static partial class Migrations
{
    /// <summary>
    /// Fork support. Strict step 15. Idempotent. Adds Repositories.IsFork / ParentCloneUrl and the upstream
    /// pull request columns (Parent*) on both the workspace-link and the Feature-context pull request tables.
    /// Rows written before this step read as "not a fork, no upstream pull request" until the next refresh.
    /// </summary>
    public static async Task MigrateForkSupportAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        // Tolerates tables that do not exist yet: the legacy baseline calls this before the Feature context tables are created.
        if (await TableExistsAsync(conn, "Repositories"))
        {
            await AddBooleanColumnIfMissingAsync(conn, "Repositories", "IsFork");
            await AddNullableTextColumnIfMissingAsync(conn, "Repositories", "ParentCloneUrl");
        }

        foreach (var table in new[] { "WorkspaceRepositoryPullRequests", "WorkspaceRepositoryContextPullRequests" })
        {
            if (!await TableExistsAsync(conn, table))
                continue;

            await AddNullableIntegerColumnIfMissingAsync(conn, table, "ParentPullRequestNumber");
            await AddNullableTextColumnIfMissingAsync(conn, table, "ParentState");
            await AddNullableIntegerColumnIfMissingAsync(conn, table, "ParentMergeable");
            await AddNullableTextColumnIfMissingAsync(conn, table, "ParentMergeableState");
            await AddNullableTextColumnIfMissingAsync(conn, table, "ParentHtmlUrl");
            await AddNullableTextColumnIfMissingAsync(conn, table, "ParentMergedAt");
            await AddNullableIntegerColumnIfMissingAsync(conn, table, "ParentChangedFiles");
        }

        logger?.LogDebug("Fork support columns are present.");
    }

    /// <summary>
    /// Adds Repositories.MissingFromProviderSinceUtc (nullable TEXT, EF stores DateTime as TEXT in SQLite): set when
    /// a refresh keeps a Workspace-linked repository its connector no longer returns. Strict step 17: no try/catch,
    /// a failure propagates to <see cref="RunStrictStepAsync"/>. Idempotent.
    /// </summary>
    public static async Task MigrateRepositoryMissingFromProviderColumnAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await AddNullableTextColumnIfMissingAsync(conn, "Repositories", "MissingFromProviderSinceUtc");
    }

    /// <summary>
    /// Adds Connectors.RemovedAtUtc (nullable TEXT): set when a connector is removed while a Workspace still uses
    /// one of its repositories. Strict step 18: no try/catch, a failure propagates to
    /// <see cref="RunStrictStepAsync"/>. Idempotent.
    /// </summary>
    public static async Task MigrateConnectorRemovedAtColumnAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await AddNullableTextColumnIfMissingAsync(conn, "Connectors", "RemovedAtUtc");
    }

    /// <summary>
    /// Reference (read-only) repositories. Strict step 19: no try/catch, a failure propagates to
    /// <see cref="RunStrictStepAsync"/>. Idempotent. Adds Workspaces.ReferenceDefault, WorkspaceRepositories.IsReference
    /// and WorkspaceFeatureRepositories.IsReferenceSnapshot / SourceBranchName. Every existing row reads as writable.
    /// Older steps load these tables through EF, whose model already has the columns, so the runner also calls this
    /// before them (see <see cref="RunAllAsync(AppDbContext, ILogger?, TimeProvider?)"/>).
    /// </summary>
    public static async Task MigrateReferenceRepositoriesAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        // Tolerates tables that do not exist yet: it also runs ahead of older steps, before the Feature tables are created.
        if (await TableExistsAsync(conn, "Workspaces"))
            await AddBooleanColumnIfMissingAsync(conn, "Workspaces", "ReferenceDefault");

        if (await TableExistsAsync(conn, "WorkspaceRepositories"))
            await AddBooleanColumnIfMissingAsync(conn, "WorkspaceRepositories", "IsReference");

        if (await TableExistsAsync(conn, "WorkspaceFeatureRepositories"))
        {
            await AddBooleanColumnIfMissingAsync(conn, "WorkspaceFeatureRepositories", "IsReferenceSnapshot");
            await AddNullableTextColumnIfMissingAsync(conn, "WorkspaceFeatureRepositories", "SourceBranchName");
        }

        logger?.LogDebug("Reference repository columns are present.");
    }

    private static async Task<bool> TableExistsAsync(System.Data.Common.DbConnection conn, string tableName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{tableName}'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task AddBooleanColumnIfMissingAsync(System.Data.Common.DbConnection conn, string tableName, string columnName)
    {
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = '{columnName}'";
        if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0)
            return;

        await using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} INTEGER NOT NULL DEFAULT 0";
        await alterCmd.ExecuteNonQueryAsync();
    }
}
