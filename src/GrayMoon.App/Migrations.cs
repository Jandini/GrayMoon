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

    /// <summary>Creates WorkspaceRepositoryActions table for persisted CI action status per workspace–repository link.</summary>
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
}
