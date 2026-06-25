using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App;
/// <summary>SQLite schema migrations run at startup after EnsureCreated. Each method is idempotent.</summary>
public static class Migrations
{
    public static async Task RunAllAsync(AppDbContext dbContext)
    {
        await MigrateRepositoriesTopicsAsync(dbContext);
        await MigrateRepositoriesArchivedAsync(dbContext);
        await MigrateWorkspaceSyncMetadataAsync(dbContext);
        await MigrateConnectorUserNameAsync(dbContext);
        await MigrateConnectorTypeAndTokenAsync(dbContext);
        await MigrateConnectorUserTokenEncryptionAsync(dbContext);
        await MigrateConnectorTokenHealthAsync(dbContext);
        await MigrateWorkspaceRepositoriesSyncStatusAsync(dbContext);
        await MigrateWorkspaceRepositoriesProjectsAsync(dbContext);
        await MigrateWorkspaceRepositoriesCommitsAsync(dbContext);
        await MigrateWorkspaceRepositoriesBranchHasUpstreamAsync(dbContext);
        await MigrateWorkspaceRepositoriesDependencyLevelAndDependenciesAsync(dbContext);
        await MigrateWorkspaceRepositoriesDefaultBranchDivergenceAsync(dbContext);
        await MigrateWorkspaceRepositoriesDefaultBranchNameAsync(dbContext);
        await MigrateWorkspaceRepositoriesSyncStatusWhenDefaultBranchMissingAsync(dbContext);
        await MigrateWorkspaceProjectsMatchedConnectorAsync(dbContext);
        await MigrateRepositoryBranchesAsync(dbContext);
        await MigrateRepositoryBranchesIsDefaultAsync(dbContext);
        await MigrateWorkspaceFilesAsync(dbContext);
        await MigrateWorkspaceFileVersionConfigsAsync(dbContext);
        await MigrateSettingsAsync(dbContext);
        await MigrateWorkspaceRootPathAsync(dbContext);
        await MigrateWorkspaceRepositoryPullRequestsAsync(dbContext);
        await MigrateWorkspaceRepositoryPullRequestsChangedFilesAsync(dbContext);
        await MigrateWorkspaceRepositoryActionsAsync(dbContext);
        await MigrateWorkspaceRepositoryActionsWorkflowsJsonAsync(dbContext);
        await MigrateWorkspaceRepositoriesRepositoryTypeAsync(dbContext);
        await MigrateRepositoryProviderIdAsync(dbContext);
        await MigrateWorkspaceRepositoriesCheckedOutTagAsync(dbContext);
        await MigrateRepositoryBranchesIsTagAsync(dbContext);
        await MigrateRepositoryBranchesSortIndexAsync(dbContext);
        await MigrateWorkspaceRepositoriesHasNewerTagAsync(dbContext);
        await MigrateWorkspaceFileLineStatusTableAsync(dbContext);
        await MigrateOutOfDateFileLinesColumnAsync(dbContext);
        await MigrateTotalFileLinesColumnAsync(dbContext);
    }

    public static async Task MigrateRepositoriesTopicsAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Repositories') WHERE name='Topics'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE Repositories ADD COLUMN Topics TEXT";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Adds Archived column to Repositories for marking GitHub archived repositories.</summary>
    public static async Task MigrateRepositoriesArchivedAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Repositories') WHERE name='Archived'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE Repositories ADD COLUMN Archived INTEGER NOT NULL DEFAULT 0";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>
    /// One-time migration to protect existing connector UserToken values using the current token protector (AES-GCM).
    /// Idempotent: skips values that already use the v2: scheme.
    /// </summary>
    public static async Task MigrateConnectorUserTokenEncryptionAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            // Ensure Connectors table and UserToken column exist before using EF.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Connectors'";
                var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!tableExists)
                    return;

                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connectors') WHERE name='UserToken'";
                var hasUserToken = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!hasUserToken)
                    return;
            }

            var connectors = await dbContext.Connectors
                .Where(c => !string.IsNullOrWhiteSpace(c.UserToken))
                .ToListAsync();

            if (connectors.Count == 0)
                return;

            var changed = 0;
            foreach (var connector in connectors)
            {
                var current = connector.UserToken;
                if (string.IsNullOrWhiteSpace(current))
                    continue;

                var trimmed = current.Trim();
                // Skip tokens that already use the v2: scheme (AES-GCM format).
                if (trimmed.StartsWith("v2:", StringComparison.Ordinal))
                    continue;

                // Normalize any legacy/plain/Base64 token to plaintext, then protect with the current scheme.
                var plain = ConnectorHelpers.UnprotectToken(current);
                if (string.IsNullOrWhiteSpace(plain))
                    continue;

                var protectedToken = ConnectorHelpers.ProtectToken(plain);
                if (!string.Equals(current, protectedToken, StringComparison.Ordinal))
                {
                    connector.UserToken = protectedToken;
                    changed++;
                }
            }

            if (changed > 0)
                await dbContext.SaveChangesAsync();
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Adds TokenHealth, TokenHealthError, and IsHealthy columns to Connectors for tracking token status.</summary>
    public static async Task MigrateConnectorTokenHealthAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Connectors'";
                var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!tableExists)
                    return;

                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connectors') WHERE name='TokenHealth'";
                var hasTokenHealth = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!hasTokenHealth)
                {
                    cmd.CommandText = "ALTER TABLE Connectors ADD COLUMN TokenHealth TEXT";
                    await cmd.ExecuteNonQueryAsync();
                }

                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connectors') WHERE name='TokenHealthError'";
                var hasTokenHealthError = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!hasTokenHealthError)
                {
                    cmd.CommandText = "ALTER TABLE Connectors ADD COLUMN TokenHealthError TEXT";
                    await cmd.ExecuteNonQueryAsync();
                }

                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connectors') WHERE name='IsHealthy'";
                var hasIsHealthy = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!hasIsHealthy)
                {
                    cmd.CommandText = "ALTER TABLE Connectors ADD COLUMN IsHealthy INTEGER NOT NULL DEFAULT 0";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceSyncMetadataAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Workspaces') WHERE name='LastSyncedAt'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE Workspaces ADD COLUMN LastSyncedAt TEXT";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "ALTER TABLE Workspaces ADD COLUMN IsInSync INTEGER NOT NULL DEFAULT 0";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateConnectorUserNameAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connectors') WHERE name='UserName'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE Connectors ADD COLUMN UserName TEXT";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateConnectorTypeAndTokenAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connectors') WHERE name='ConnectorType'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE Connectors ADD COLUMN ConnectorType INTEGER NOT NULL DEFAULT 1";
                    await cmd.ExecuteNonQueryAsync();
                }

                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Connectors') WHERE name='UserToken' AND \"notnull\"=1";
                count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count > 0)
                {
                    cmd.CommandText = "UPDATE Connectors SET UserToken = '' WHERE UserToken IS NULL";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceRepositoriesSyncStatusAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='SyncStatus'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN SyncStatus INTEGER NOT NULL DEFAULT 4";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceRepositoriesProjectsAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='Projects'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN Projects INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceRepositoriesCommitsAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='OutgoingCommits'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN OutgoingCommits INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='IncomingCommits'";
                count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN IncomingCommits INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceRepositoriesBranchHasUpstreamAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='BranchHasUpstream'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN BranchHasUpstream INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceRepositoriesDependencyLevelAndDependenciesAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='DependencyLevel'";
                var hasDependencyLevel = Convert.ToInt32(await cmd.ExecuteScalarAsync()) != 0;
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='Sequence'";
                var hasSequence = Convert.ToInt32(await cmd.ExecuteScalarAsync()) != 0;

                if (!hasDependencyLevel)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN DependencyLevel INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                    if (hasSequence)
                    {
                        cmd.CommandText = "UPDATE WorkspaceRepositories SET DependencyLevel = Sequence";
                        await cmd.ExecuteNonQueryAsync();
                        cmd.CommandText = "CREATE TABLE WorkspaceRepositories_new(WorkspaceRepositoryId INTEGER PRIMARY KEY AUTOINCREMENT, WorkspaceId INTEGER NOT NULL, RepositoryId INTEGER NOT NULL, GitVersion TEXT, BranchName TEXT, Projects INTEGER, OutgoingCommits INTEGER, IncomingCommits INTEGER, SyncStatus INTEGER NOT NULL, DependencyLevel INTEGER, Dependencies INTEGER, UnmatchedDeps INTEGER)";
                        await cmd.ExecuteNonQueryAsync();
                        cmd.CommandText = "INSERT INTO WorkspaceRepositories_new SELECT WorkspaceRepositoryId, WorkspaceId, RepositoryId, GitVersion, BranchName, Projects, OutgoingCommits, IncomingCommits, SyncStatus, DependencyLevel, Dependencies, UnmatchedDeps FROM WorkspaceRepositories";
                        await cmd.ExecuteNonQueryAsync();
                        cmd.CommandText = "DROP TABLE WorkspaceRepositories";
                        await cmd.ExecuteNonQueryAsync();
                        cmd.CommandText = "ALTER TABLE WorkspaceRepositories_new RENAME TO WorkspaceRepositories";
                        await cmd.ExecuteNonQueryAsync();
                        cmd.CommandText = "CREATE UNIQUE INDEX IX_WorkspaceRepositories_WorkspaceId_RepositoryId ON WorkspaceRepositories(WorkspaceId, RepositoryId)";
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                else if (hasSequence)
                {
                    cmd.CommandText = "CREATE TABLE WorkspaceRepositories_new(WorkspaceRepositoryId INTEGER PRIMARY KEY AUTOINCREMENT, WorkspaceId INTEGER NOT NULL, RepositoryId INTEGER NOT NULL, GitVersion TEXT, BranchName TEXT, Projects INTEGER, OutgoingCommits INTEGER, IncomingCommits INTEGER, SyncStatus INTEGER NOT NULL, DependencyLevel INTEGER, Dependencies INTEGER, UnmatchedDeps INTEGER)";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "INSERT INTO WorkspaceRepositories_new SELECT WorkspaceRepositoryId, WorkspaceId, RepositoryId, GitVersion, BranchName, Projects, OutgoingCommits, IncomingCommits, SyncStatus, DependencyLevel, Dependencies, UnmatchedDeps FROM WorkspaceRepositories";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "DROP TABLE WorkspaceRepositories";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories_new RENAME TO WorkspaceRepositories";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "CREATE UNIQUE INDEX IX_WorkspaceRepositories_WorkspaceId_RepositoryId ON WorkspaceRepositories(WorkspaceId, RepositoryId)";
                    await cmd.ExecuteNonQueryAsync();
                }

                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='Dependencies'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN Dependencies INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='UnmatchedDeps'";
                count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN UnmatchedDeps INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceRepositoriesDefaultBranchDivergenceAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='DefaultBranchBehindCommits'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN DefaultBranchBehindCommits INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='DefaultBranchAheadCommits'";
                count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN DefaultBranchAheadCommits INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceRepositoriesDefaultBranchNameAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='DefaultBranchName'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN DefaultBranchName TEXT";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Set SyncStatus to NeedsSync for any link where DefaultBranchName is null or empty, so existing data shows "sync" until a full sync persists the default branch.</summary>
    public static async Task MigrateWorkspaceRepositoriesSyncStatusWhenDefaultBranchMissingAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='DefaultBranchName'";
                var hasColumn = Convert.ToInt32(await cmd.ExecuteScalarAsync()) != 0;
                if (!hasColumn)
                    return;

                var needsSyncValue = (int)RepoSyncStatus.NeedsSync;
                cmd.CommandText = $"UPDATE WorkspaceRepositories SET SyncStatus = {needsSyncValue} WHERE (DefaultBranchName IS NULL OR trim(DefaultBranchName) = '')";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceProjectsMatchedConnectorAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceProjects') WHERE name='MatchedConnectorId'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceProjects ADD COLUMN MatchedConnectorId INTEGER REFERENCES Connectors(ConnectorId)";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateRepositoryBranchesAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='RepositoryBranches'";
                var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

                if (!tableExists)
                {
                    cmd.CommandText = @"
                    CREATE TABLE RepositoryBranches (
                        RepositoryBranchId INTEGER PRIMARY KEY AUTOINCREMENT,
                        WorkspaceRepositoryId INTEGER NOT NULL,
                        BranchName TEXT NOT NULL,
                        IsRemote INTEGER NOT NULL,
                        LastSeenAt TEXT NOT NULL,
                        FOREIGN KEY (WorkspaceRepositoryId) REFERENCES WorkspaceRepositories(WorkspaceRepositoryId) ON DELETE CASCADE,
                        UNIQUE(WorkspaceRepositoryId, BranchName, IsRemote)
                    )";
                    await cmd.ExecuteNonQueryAsync();

                    cmd.CommandText = "CREATE INDEX IX_RepositoryBranches_WorkspaceRepositoryId ON RepositoryBranches(WorkspaceRepositoryId)";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateRepositoryBranchesIsDefaultAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('RepositoryBranches') WHERE name='IsDefault'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE RepositoryBranches ADD COLUMN IsDefault INTEGER NOT NULL DEFAULT 0";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceFilesAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WorkspaceFiles'";
                var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

                if (!tableExists)
                {
                    cmd.CommandText = @"
                    CREATE TABLE WorkspaceFiles (
                        FileId INTEGER PRIMARY KEY AUTOINCREMENT,
                        WorkspaceId INTEGER NOT NULL,
                        RepositoryId INTEGER NOT NULL,
                        FileName TEXT NOT NULL,
                        FilePath TEXT NOT NULL,
                        FOREIGN KEY (WorkspaceId) REFERENCES Workspaces(WorkspaceId) ON DELETE CASCADE,
                        FOREIGN KEY (RepositoryId) REFERENCES Repositories(RepositoryId) ON DELETE CASCADE,
                        UNIQUE(WorkspaceId, RepositoryId, FilePath)
                    )";
                    await cmd.ExecuteNonQueryAsync();

                    cmd.CommandText = "CREATE INDEX IX_WorkspaceFiles_WorkspaceId ON WorkspaceFiles(WorkspaceId)";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceFileVersionConfigsAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WorkspaceFileVersionConfigs'";
                var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

                if (!tableExists)
                {
                    cmd.CommandText = @"
                    CREATE TABLE WorkspaceFileVersionConfigs (
                        ConfigId INTEGER PRIMARY KEY AUTOINCREMENT,
                        FileId INTEGER NOT NULL UNIQUE,
                        VersionPattern TEXT NOT NULL,
                        FOREIGN KEY (FileId) REFERENCES WorkspaceFiles(FileId) ON DELETE CASCADE
                    )";
                    await cmd.ExecuteNonQueryAsync();

                    cmd.CommandText = "CREATE INDEX IX_WorkspaceFileVersionConfigs_FileId ON WorkspaceFileVersionConfigs(FileId)";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateSettingsAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Settings'";
                var settingsExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

                if (!settingsExists)
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AppSettings'";
                    var oldTableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

                    if (oldTableExists)
                    {
                        cmd.CommandText = "ALTER TABLE AppSettings RENAME TO Settings";
                        await cmd.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        cmd.CommandText = @"
                        CREATE TABLE Settings (
                            SettingId INTEGER PRIMARY KEY AUTOINCREMENT,
                            Key TEXT NOT NULL,
                            Value TEXT,
                            UNIQUE(Key)
                        )";
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Settings') WHERE name='AppSettingId'";
                var oldColumnExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (oldColumnExists)
                {
                    cmd.CommandText = "ALTER TABLE Settings RENAME COLUMN AppSettingId TO SettingId";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceRootPathAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Workspaces') WHERE name='RootPath'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    cmd.CommandText = "ALTER TABLE Workspaces ADD COLUMN RootPath TEXT";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    public static async Task MigrateWorkspaceRepositoryPullRequestsAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WorkspaceRepositoryPullRequests'";
                var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

                if (!tableExists)
                {
                    cmd.CommandText = @"
                    CREATE TABLE WorkspaceRepositoryPullRequests (
                        WorkspaceRepositoryId INTEGER PRIMARY KEY,
                        PullRequestNumber INTEGER,
                        State TEXT,
                        Mergeable INTEGER,
                        MergeableState TEXT,
                        HtmlUrl TEXT,
                        MergedAt TEXT,
                        LastCheckedAt TEXT NOT NULL,
                        FOREIGN KEY (WorkspaceRepositoryId) REFERENCES WorkspaceRepositories(WorkspaceRepositoryId) ON DELETE CASCADE
                    )";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Creates WorkspaceRepositoryActions table for persisted CI action status per workspace-repository link.</summary>
    public static async Task MigrateWorkspaceRepositoryActionsAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WorkspaceRepositoryActions'";
                var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

                if (!tableExists)
                {
                    cmd.CommandText = @"
                    CREATE TABLE WorkspaceRepositoryActions (
                        WorkspaceRepositoryId INTEGER PRIMARY KEY,
                        Status TEXT,
                        HtmlUrl TEXT,
                        UpdatedAt TEXT,
                        BranchName TEXT,
                        RunId INTEGER,
                        WorkflowId INTEGER,
                        WorkflowName TEXT,
                        LastCheckedAt TEXT NOT NULL,
                        FOREIGN KEY (WorkspaceRepositoryId) REFERENCES WorkspaceRepositories(WorkspaceRepositoryId) ON DELETE CASCADE
                    )";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Adds WorkflowsJson column for per-workflow CI status list.</summary>
    public static async Task MigrateWorkspaceRepositoryActionsWorkflowsJsonAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WorkspaceRepositoryActions'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                    return;

                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositoryActions') WHERE name='WorkflowsJson'";
                var hasColumn = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!hasColumn)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositoryActions ADD COLUMN WorkflowsJson TEXT";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Adds RepositoryType column to WorkspaceRepositories to store the dominant project type (Service, Package, etc.).</summary>
    public static async Task MigrateWorkspaceRepositoriesRepositoryTypeAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='RepositoryType'";
                var hasColumn = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!hasColumn)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN RepositoryType INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Adds ChangedFiles column to WorkspaceRepositoryPullRequests to store PR changed file count.</summary>
    public static async Task MigrateWorkspaceRepositoryPullRequestsChangedFilesAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WorkspaceRepositoryPullRequests'";
                var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!tableExists)
                    return;

                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositoryPullRequests') WHERE name='ChangedFiles'";
                var hasColumn = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!hasColumn)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositoryPullRequests ADD COLUMN ChangedFiles INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Adds CheckedOutTag column to WorkspaceRepositories so a repository can be persisted as pinned to a tag (detached HEAD).</summary>
    public static async Task MigrateWorkspaceRepositoriesCheckedOutTagAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='CheckedOutTag'";
                var hasColumn = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!hasColumn)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN CheckedOutTag TEXT";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Adds IsTag column to RepositoryBranches so tag rows can be persisted alongside branches.</summary>
    public static async Task MigrateRepositoryBranchesIsTagAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='RepositoryBranches'";
                var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!tableExists)
                    return;

                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('RepositoryBranches') WHERE name='IsTag'";
                var hasColumn = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!hasColumn)
                {
                    cmd.CommandText = "ALTER TABLE RepositoryBranches ADD COLUMN IsTag INTEGER NOT NULL DEFAULT 0";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Adds HasNewerTag column to WorkspaceRepositories so the UI can show an "upgrade" badge when a newer tag exists for a tag-pinned repo.</summary>
    public static async Task MigrateWorkspaceRepositoriesHasNewerTagAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='HasNewerTag'";
                var hasColumn = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!hasColumn)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN HasNewerTag INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Creates the WorkspaceFileLineStatuses table that persists per-line staleness for configured version files.</summary>
    public static async Task MigrateWorkspaceFileLineStatusTableAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WorkspaceFileLineStatuses'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                {
                    cmd.CommandText = @"CREATE TABLE WorkspaceFileLineStatuses (
                        StatusId INTEGER PRIMARY KEY AUTOINCREMENT,
                        WorkspaceId INTEGER NOT NULL,
                        RepositoryId INTEGER NOT NULL,
                        FilePath TEXT NOT NULL,
                        FileName TEXT NOT NULL,
                        TotalMatchedLines INTEGER NOT NULL DEFAULT 0,
                        OutOfDateLines INTEGER NOT NULL DEFAULT 0
                    )";
                    await cmd.ExecuteNonQueryAsync();
                }

                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_WorkspaceFileLineStatuses_Unique'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                {
                    cmd.CommandText = "CREATE UNIQUE INDEX IX_WorkspaceFileLineStatuses_Unique ON WorkspaceFileLineStatuses(WorkspaceId, RepositoryId, FilePath)";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Adds OutOfDateFileLines column to WorkspaceRepositories for the file-version staleness badge X count.</summary>
    public static async Task MigrateOutOfDateFileLinesColumnAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='OutOfDateFileLines'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN OutOfDateFileLines INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Adds TotalFileLines column to WorkspaceRepositories for the file-version staleness badge Y denominator.</summary>
    public static async Task MigrateTotalFileLinesColumnAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositories') WHERE name='TotalFileLines'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                {
                    cmd.CommandText = "ALTER TABLE WorkspaceRepositories ADD COLUMN TotalFileLines INTEGER";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>Adds SortIndex column to RepositoryBranches so tags can be persisted in the agent-reported order (newest first).</summary>
    public static async Task MigrateRepositoryBranchesSortIndexAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='RepositoryBranches'";
                var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!tableExists)
                    return;

                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('RepositoryBranches') WHERE name='SortIndex'";
                var hasColumn = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                if (!hasColumn)
                {
                    cmd.CommandText = "ALTER TABLE RepositoryBranches ADD COLUMN SortIndex INTEGER NOT NULL DEFAULT 0";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }

    /// <summary>
    /// Adds GitHubRepositoryId (stable provider numeric ID) and NodeId (stable provider node ID) columns to
    /// Repositories, drops the old unique name/org index (which blocks renames), and creates the new
    /// filtered unique index on (ConnectorId, GitHubRepositoryId) for rows that have a provider ID.
    /// Idempotent.
    /// </summary>
    public static async Task MigrateRepositoryProviderIdAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Repositories'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                    return;

                // Add GitHubRepositoryId column
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Repositories') WHERE name='GitHubRepositoryId'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                {
                    cmd.CommandText = "ALTER TABLE Repositories ADD COLUMN GitHubRepositoryId INTEGER NOT NULL DEFAULT 0";
                    await cmd.ExecuteNonQueryAsync();
                }

                // Add NodeId column
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Repositories') WHERE name='NodeId'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                {
                    cmd.CommandText = "ALTER TABLE Repositories ADD COLUMN NodeId TEXT";
                    await cmd.ExecuteNonQueryAsync();
                }

                // Drop the old unique name/org index that blocks renames - make it non-unique
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_Repositories_ConnectorId_RepositoryName_OrgName'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0)
                {
                    cmd.CommandText = "DROP INDEX IX_Repositories_ConnectorId_RepositoryName_OrgName";
                    await cmd.ExecuteNonQueryAsync();
                    // Re-create as non-unique
                    cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_Repositories_ConnectorId_Name_Org ON Repositories(ConnectorId, RepositoryName, OrgName)";
                    await cmd.ExecuteNonQueryAsync();
                }

                // Create the stable-identity unique filtered index
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_Repositories_ConnectorId_GitHubRepositoryId'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                {
                    cmd.CommandText = "CREATE UNIQUE INDEX IX_Repositories_ConnectorId_GitHubRepositoryId ON Repositories(ConnectorId, GitHubRepositoryId) WHERE GitHubRepositoryId > 0";
                    await cmd.ExecuteNonQueryAsync();
                }

                // Create CloneUrl index for fast URL lookups
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_Repositories_CloneUrl'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                {
                    cmd.CommandText = "CREATE INDEX IX_Repositories_CloneUrl ON Repositories(CloneUrl)";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch
        {
            // Migration may already be applied or table doesn't exist yet
        }
    }
}
