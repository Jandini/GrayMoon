using System.Data.Common;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App;

public static partial class Migrations
{
    /// <summary>
    /// Additive WorkspaceFeatureContext schema + special-Workspace backfill for existing databases.
    /// EnsureCreated() covers brand-new databases from the current model; this patches older files.
    /// </summary>
    public static async Task MigrateWorkspaceFeatureContextSchemaAsync(AppDbContext dbContext)
    {
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await AddNullableTextColumnIfMissingAsync(conn, "Workspaces", "ManagedFeatureStorageRoot");

            await EnsureFeatureCoreTablesAsync(conn);
            await EnsureFeatureProjectionTablesAsync(conn);
            await EnsureProjectAndFileLineContextColumnsAsync(conn);
            await BackfillSpecialWorkspaceContextsAsync(dbContext);
        }
        catch
        {
            // Fresh DB: EnsureCreated already created the Feature tables from the model.
        }
    }

    private static async Task EnsureFeatureCoreTablesAsync(DbConnection conn)
    {
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS "WorkspaceFeatures" (
                "WorkspaceFeatureId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceFeatures" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceId" INTEGER NOT NULL,
                "Name" TEXT NOT NULL,
                "LifecycleState" INTEGER NOT NULL,
                "BaseKind" INTEGER NOT NULL,
                "BaseWorkspaceFeatureId" INTEGER NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "LastError" TEXT NULL,
                CONSTRAINT "FK_WorkspaceFeatures_Workspaces_WorkspaceId" FOREIGN KEY ("WorkspaceId") REFERENCES "Workspaces" ("WorkspaceId") ON DELETE CASCADE,
                CONSTRAINT "FK_WorkspaceFeatures_WorkspaceFeatures_BaseWorkspaceFeatureId" FOREIGN KEY ("BaseWorkspaceFeatureId") REFERENCES "WorkspaceFeatures" ("WorkspaceFeatureId") ON DELETE RESTRICT
            );
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceFeatures_WorkspaceId_Name"
            ON "WorkspaceFeatures" ("WorkspaceId", "Name");
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS "WorkspaceFeatureContexts" (
                "WorkspaceFeatureContextId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceFeatureContexts" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceId" INTEGER NOT NULL,
                "Kind" INTEGER NOT NULL,
                "WorkspaceFeatureId" INTEGER NULL,
                "CreatedAt" TEXT NOT NULL,
                "LastSyncedAt" TEXT NULL,
                "IsInSync" INTEGER NOT NULL,
                CONSTRAINT "FK_WorkspaceFeatureContexts_Workspaces_WorkspaceId" FOREIGN KEY ("WorkspaceId") REFERENCES "Workspaces" ("WorkspaceId") ON DELETE CASCADE,
                CONSTRAINT "FK_WorkspaceFeatureContexts_WorkspaceFeatures_WorkspaceFeatureId" FOREIGN KEY ("WorkspaceFeatureId") REFERENCES "WorkspaceFeatures" ("WorkspaceFeatureId") ON DELETE CASCADE
            );
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceFeatureContexts_Workspace_KindWorkspace"
            ON "WorkspaceFeatureContexts" ("WorkspaceId") WHERE "Kind" = 0;
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceFeatureContexts_FeatureId"
            ON "WorkspaceFeatureContexts" ("WorkspaceFeatureId") WHERE "WorkspaceFeatureId" IS NOT NULL;
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS "WorkspaceFeatureRepositories" (
                "WorkspaceFeatureRepositoryId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceFeatureRepositories" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceFeatureContextId" INTEGER NOT NULL,
                "WorkspaceRepositoryId" INTEGER NOT NULL,
                "WorktreePath" TEXT NOT NULL,
                "BaseCommitSha" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "State" INTEGER NOT NULL,
                "LastError" TEXT NULL,
                CONSTRAINT "FK_WorkspaceFeatureRepositories_Contexts" FOREIGN KEY ("WorkspaceFeatureContextId") REFERENCES "WorkspaceFeatureContexts" ("WorkspaceFeatureContextId") ON DELETE CASCADE,
                CONSTRAINT "FK_WorkspaceFeatureRepositories_Links" FOREIGN KEY ("WorkspaceRepositoryId") REFERENCES "WorkspaceRepositories" ("WorkspaceRepositoryId") ON DELETE CASCADE
            );
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceFeatureRepositories_Context_Repo"
            ON "WorkspaceFeatureRepositories" ("WorkspaceFeatureContextId", "WorkspaceRepositoryId");
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS "WorkspaceRepositoryContextStates" (
                "WorkspaceRepositoryContextStateId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceRepositoryContextStates" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceFeatureContextId" INTEGER NOT NULL,
                "WorkspaceRepositoryId" INTEGER NOT NULL,
                "BranchName" TEXT NULL,
                "CheckedOutTag" TEXT NULL,
                "HeadCommit" TEXT NULL,
                "HasNewerTag" INTEGER NULL,
                "GitVersion" TEXT NULL,
                "Projects" INTEGER NULL,
                "OutgoingCommits" INTEGER NULL,
                "IncomingCommits" INTEGER NULL,
                "DefaultBranchBehindCommits" INTEGER NULL,
                "DefaultBranchAheadCommits" INTEGER NULL,
                "BranchHasUpstream" INTEGER NULL,
                "SyncStatus" INTEGER NOT NULL DEFAULT 4,
                "DependencyLevel" INTEGER NULL,
                "Dependencies" INTEGER NULL,
                "UnmatchedDeps" INTEGER NULL,
                "OutOfDateFileLines" INTEGER NULL,
                "OutOfDateFileRepos" INTEGER NULL,
                "TotalFileConfigRepos" INTEGER NULL,
                "HasSelfFileVersionToken" INTEGER NULL,
                "TotalFileLines" INTEGER NULL,
                "RepositoryType" INTEGER NULL,
                CONSTRAINT "FK_WorkspaceRepositoryContextStates_Contexts" FOREIGN KEY ("WorkspaceFeatureContextId") REFERENCES "WorkspaceFeatureContexts" ("WorkspaceFeatureContextId") ON DELETE CASCADE,
                CONSTRAINT "FK_WorkspaceRepositoryContextStates_Links" FOREIGN KEY ("WorkspaceRepositoryId") REFERENCES "WorkspaceRepositories" ("WorkspaceRepositoryId") ON DELETE CASCADE
            );
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceRepositoryContextStates_Context_Repo"
            ON "WorkspaceRepositoryContextStates" ("WorkspaceFeatureContextId", "WorkspaceRepositoryId");
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS "WorkspaceSelectedFeatureContexts" (
                "WorkspaceId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceSelectedFeatureContexts" PRIMARY KEY,
                "WorkspaceFeatureContextId" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_WorkspaceSelectedFeatureContexts_Workspaces" FOREIGN KEY ("WorkspaceId") REFERENCES "Workspaces" ("WorkspaceId") ON DELETE CASCADE,
                CONSTRAINT "FK_WorkspaceSelectedFeatureContexts_Contexts" FOREIGN KEY ("WorkspaceFeatureContextId") REFERENCES "WorkspaceFeatureContexts" ("WorkspaceFeatureContextId") ON DELETE CASCADE
            );
            """);
    }

    private static async Task EnsureFeatureProjectionTablesAsync(DbConnection conn)
    {
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS "WorkspaceRepositoryContextPullRequests" (
                "WorkspaceRepositoryContextPullRequestId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceRepositoryContextPullRequests" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceFeatureContextId" INTEGER NOT NULL,
                "WorkspaceRepositoryId" INTEGER NOT NULL,
                "PullRequestNumber" INTEGER NULL,
                "State" TEXT NULL,
                "Mergeable" INTEGER NULL,
                "MergeableState" TEXT NULL,
                "HtmlUrl" TEXT NULL,
                "MergedAt" TEXT NULL,
                "ChangedFiles" INTEGER NULL,
                "LastCheckedAt" TEXT NOT NULL,
                CONSTRAINT "FK_ContextPRs_Contexts" FOREIGN KEY ("WorkspaceFeatureContextId") REFERENCES "WorkspaceFeatureContexts" ("WorkspaceFeatureContextId") ON DELETE CASCADE,
                CONSTRAINT "FK_ContextPRs_Links" FOREIGN KEY ("WorkspaceRepositoryId") REFERENCES "WorkspaceRepositories" ("WorkspaceRepositoryId") ON DELETE CASCADE
            );
            """);
        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceRepositoryContextPullRequests_Context_Repo"
            ON "WorkspaceRepositoryContextPullRequests" ("WorkspaceFeatureContextId", "WorkspaceRepositoryId");
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS "WorkspaceRepositoryContextActions" (
                "WorkspaceRepositoryContextActionId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceRepositoryContextActions" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceFeatureContextId" INTEGER NOT NULL,
                "WorkspaceRepositoryId" INTEGER NOT NULL,
                "Status" TEXT NULL,
                "HtmlUrl" TEXT NULL,
                "UpdatedAt" TEXT NULL,
                "BranchName" TEXT NULL,
                "RunId" INTEGER NULL,
                "WorkflowId" INTEGER NULL,
                "WorkflowName" TEXT NULL,
                "WorkflowsJson" TEXT NULL,
                "LastCheckedAt" TEXT NOT NULL,
                CONSTRAINT "FK_ContextActions_Contexts" FOREIGN KEY ("WorkspaceFeatureContextId") REFERENCES "WorkspaceFeatureContexts" ("WorkspaceFeatureContextId") ON DELETE CASCADE,
                CONSTRAINT "FK_ContextActions_Links" FOREIGN KEY ("WorkspaceRepositoryId") REFERENCES "WorkspaceRepositories" ("WorkspaceRepositoryId") ON DELETE CASCADE
            );
            """);
        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceRepositoryContextActions_Context_Repo"
            ON "WorkspaceRepositoryContextActions" ("WorkspaceFeatureContextId", "WorkspaceRepositoryId");
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS "WorkspaceGitContextRepositoryStatuses" (
                "WorkspaceGitContextRepositoryStatusId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceGitContextRepositoryStatuses" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceFeatureContextId" INTEGER NOT NULL,
                "WorkspaceRepositoryId" INTEGER NOT NULL,
                "SnapshotVersion" INTEGER NOT NULL,
                "BranchName" TEXT NULL,
                "HeadCommit" TEXT NULL,
                "IsDetachedHead" INTEGER NOT NULL,
                "IsUnbornBranch" INTEGER NOT NULL,
                "IsMerging" INTEGER NOT NULL,
                "IsRebasing" INTEGER NOT NULL,
                "IsCherryPicking" INTEGER NOT NULL,
                "StagedCount" INTEGER NOT NULL,
                "ChangedCount" INTEGER NOT NULL,
                "ConflictCount" INTEGER NOT NULL,
                "Insertions" INTEGER NULL,
                "Deletions" INTEGER NULL,
                "StagedInsertions" INTEGER NULL,
                "StagedDeletions" INTEGER NULL,
                "AgentScannedAt" TEXT NOT NULL,
                "PersistedAt" TEXT NOT NULL,
                "LastErrorCode" TEXT NULL,
                "LastErrorMessage" TEXT NULL,
                CONSTRAINT "FK_GitContextStatus_Contexts" FOREIGN KEY ("WorkspaceFeatureContextId") REFERENCES "WorkspaceFeatureContexts" ("WorkspaceFeatureContextId") ON DELETE CASCADE,
                CONSTRAINT "FK_GitContextStatus_Links" FOREIGN KEY ("WorkspaceRepositoryId") REFERENCES "WorkspaceRepositories" ("WorkspaceRepositoryId") ON DELETE CASCADE
            );
            """);
        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceGitContextRepositoryStatuses_Context_Repo"
            ON "WorkspaceGitContextRepositoryStatuses" ("WorkspaceFeatureContextId", "WorkspaceRepositoryId");
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS "WorkspaceGitContextChangeEntries" (
                "WorkspaceGitContextChangeEntryId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceGitContextChangeEntries" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceFeatureContextId" INTEGER NOT NULL,
                "WorkspaceRepositoryId" INTEGER NOT NULL,
                "Path" TEXT NOT NULL,
                "OriginalPath" TEXT NULL,
                "IndexChange" INTEGER NOT NULL,
                "WorktreeChange" INTEGER NOT NULL,
                "IsTracked" INTEGER NOT NULL,
                "IsConflicted" INTEGER NOT NULL,
                "IsSubmodule" INTEGER NOT NULL,
                CONSTRAINT "FK_GitContextEntries_Contexts" FOREIGN KEY ("WorkspaceFeatureContextId") REFERENCES "WorkspaceFeatureContexts" ("WorkspaceFeatureContextId") ON DELETE CASCADE,
                CONSTRAINT "FK_GitContextEntries_Links" FOREIGN KEY ("WorkspaceRepositoryId") REFERENCES "WorkspaceRepositories" ("WorkspaceRepositoryId") ON DELETE CASCADE
            );
            """);
        await ExecuteNonQueryAsync(conn, """
            CREATE INDEX IF NOT EXISTS "IX_WorkspaceGitContextChangeEntries_Context_Repo"
            ON "WorkspaceGitContextChangeEntries" ("WorkspaceFeatureContextId", "WorkspaceRepositoryId");
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS "WorkspaceFileContextStates" (
                "WorkspaceFileContextStateId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceFileContextStates" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceFeatureContextId" INTEGER NOT NULL,
                "FileId" INTEGER NOT NULL,
                "IsMissingOnDisk" INTEGER NULL,
                "LastCheckedAt" TEXT NULL,
                CONSTRAINT "FK_FileContextStates_Contexts" FOREIGN KEY ("WorkspaceFeatureContextId") REFERENCES "WorkspaceFeatureContexts" ("WorkspaceFeatureContextId") ON DELETE CASCADE,
                CONSTRAINT "FK_FileContextStates_Files" FOREIGN KEY ("FileId") REFERENCES "WorkspaceFiles" ("FileId") ON DELETE CASCADE
            );
            """);
        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceFileContextStates_Context_File"
            ON "WorkspaceFileContextStates" ("WorkspaceFeatureContextId", "FileId");
            """);
    }

    private static async Task EnsureProjectAndFileLineContextColumnsAsync(DbConnection conn)
    {
        await AddNullableIntegerColumnIfMissingAsync(conn, "WorkspaceProjects", "WorkspaceFeatureContextId");
        await AddNullableIntegerColumnIfMissingAsync(conn, "WorkspaceFileLineStatuses", "WorkspaceFeatureContextId");

        // Prefer context-scoped uniqueness when the column exists; drop legacy unique index if present.
        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceProjects_Context_Repo_Name"
            ON "WorkspaceProjects" ("WorkspaceFeatureContextId", "RepositoryId", "ProjectName")
            WHERE "WorkspaceFeatureContextId" IS NOT NULL;
            """);

        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceFileLineStatuses_Context_Repo_Path_Token"
            ON "WorkspaceFileLineStatuses" ("WorkspaceFeatureContextId", "RepositoryId", "FilePath", "TokenName")
            WHERE "WorkspaceFeatureContextId" IS NOT NULL;
            """);
    }

    private static async Task BackfillSpecialWorkspaceContextsAsync(AppDbContext dbContext)
    {
        var now = DateTime.UtcNow;
        var workspaces = await dbContext.Workspaces.AsNoTracking().Select(w => new { w.WorkspaceId, w.LastSyncedAt, w.IsInSync }).ToListAsync();
        if (workspaces.Count == 0)
            return;

        foreach (var workspace in workspaces)
        {
            var context = await dbContext.WorkspaceFeatureContexts
                .FirstOrDefaultAsync(c => c.WorkspaceId == workspace.WorkspaceId && c.Kind == WorkspaceFeatureContextKind.Workspace);

            if (context is null)
            {
                context = new WorkspaceFeatureContext
                {
                    WorkspaceId = workspace.WorkspaceId,
                    Kind = WorkspaceFeatureContextKind.Workspace,
                    WorkspaceFeatureId = null,
                    CreatedAt = now,
                    LastSyncedAt = workspace.LastSyncedAt,
                    IsInSync = workspace.IsInSync
                };
                dbContext.WorkspaceFeatureContexts.Add(context);
                await dbContext.SaveChangesAsync();
            }

            var links = await dbContext.WorkspaceRepositories
                .AsNoTracking()
                .Where(l => l.WorkspaceId == workspace.WorkspaceId)
                .ToListAsync();

            foreach (var link in links)
            {
                var exists = await dbContext.WorkspaceRepositoryContextStates
                    .AnyAsync(s => s.WorkspaceFeatureContextId == context.WorkspaceFeatureContextId && s.WorkspaceRepositoryId == link.WorkspaceRepositoryId);
                if (exists)
                    continue;

                dbContext.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
                {
                    WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                    WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                    BranchName = link.BranchName,
                    CheckedOutTag = link.CheckedOutTag,
                    HasNewerTag = link.HasNewerTag,
                    GitVersion = link.GitVersion,
                    Projects = link.Projects,
                    OutgoingCommits = link.OutgoingCommits,
                    IncomingCommits = link.IncomingCommits,
                    DefaultBranchBehindCommits = link.DefaultBranchBehindCommits,
                    DefaultBranchAheadCommits = link.DefaultBranchAheadCommits,
                    BranchHasUpstream = link.BranchHasUpstream,
                    SyncStatus = link.SyncStatus,
                    DependencyLevel = link.DependencyLevel,
                    Dependencies = link.Dependencies,
                    UnmatchedDeps = link.UnmatchedDeps,
                    OutOfDateFileLines = link.OutOfDateFileLines,
                    OutOfDateFileRepos = link.OutOfDateFileRepos,
                    TotalFileConfigRepos = link.TotalFileConfigRepos,
                    HasSelfFileVersionToken = link.HasSelfFileVersionToken,
                    TotalFileLines = link.TotalFileLines,
                    RepositoryType = link.RepositoryType
                });
            }

            await dbContext.SaveChangesAsync();

            // Backfill projects / file line statuses / file missing / PR / Actions / Git Changes onto special context.
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "WorkspaceProjects"
                SET "WorkspaceFeatureContextId" = {0}
                WHERE "WorkspaceId" = {1} AND ("WorkspaceFeatureContextId" IS NULL OR "WorkspaceFeatureContextId" = 0)
                """,
                context.WorkspaceFeatureContextId, workspace.WorkspaceId);

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "WorkspaceFileLineStatuses"
                SET "WorkspaceFeatureContextId" = {0}
                WHERE "WorkspaceId" = {1} AND ("WorkspaceFeatureContextId" IS NULL OR "WorkspaceFeatureContextId" = 0)
                """,
                context.WorkspaceFeatureContextId, workspace.WorkspaceId);

            await BackfillFileContextStatesAsync(dbContext, context.WorkspaceFeatureContextId, workspace.WorkspaceId);
            await BackfillContextPullRequestsAsync(dbContext, context.WorkspaceFeatureContextId, workspace.WorkspaceId);
            await BackfillContextActionsAsync(dbContext, context.WorkspaceFeatureContextId, workspace.WorkspaceId);
            await BackfillContextGitChangesAsync(dbContext, context.WorkspaceFeatureContextId, workspace.WorkspaceId);

            var selected = await dbContext.WorkspaceSelectedFeatureContexts.FindAsync(workspace.WorkspaceId);
            if (selected is null)
            {
                dbContext.WorkspaceSelectedFeatureContexts.Add(new WorkspaceSelectedFeatureContext
                {
                    WorkspaceId = workspace.WorkspaceId,
                    WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                    UpdatedAt = now
                });
                await dbContext.SaveChangesAsync();
            }
        }
    }

    private static async Task BackfillFileContextStatesAsync(AppDbContext dbContext, int contextId, int workspaceId)
    {
        var files = await dbContext.WorkspaceFiles.AsNoTracking()
            .Where(f => f.WorkspaceId == workspaceId)
            .Select(f => new { f.FileId, f.IsMissingOnDisk })
            .ToListAsync();

        foreach (var file in files)
        {
            var exists = await dbContext.WorkspaceFileContextStates
                .AnyAsync(s => s.WorkspaceFeatureContextId == contextId && s.FileId == file.FileId);
            if (exists)
                continue;

            dbContext.WorkspaceFileContextStates.Add(new WorkspaceFileContextState
            {
                WorkspaceFeatureContextId = contextId,
                FileId = file.FileId,
                IsMissingOnDisk = file.IsMissingOnDisk,
                LastCheckedAt = null
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private static async Task BackfillContextPullRequestsAsync(AppDbContext dbContext, int contextId, int workspaceId)
    {
        var rows = await (
            from pr in dbContext.WorkspaceRepositoryPullRequests.AsNoTracking()
            join link in dbContext.WorkspaceRepositories.AsNoTracking() on pr.WorkspaceRepositoryId equals link.WorkspaceRepositoryId
            where link.WorkspaceId == workspaceId
            select pr).ToListAsync();

        foreach (var pr in rows)
        {
            var exists = await dbContext.WorkspaceRepositoryContextPullRequests
                .AnyAsync(x => x.WorkspaceFeatureContextId == contextId && x.WorkspaceRepositoryId == pr.WorkspaceRepositoryId);
            if (exists)
                continue;

            dbContext.WorkspaceRepositoryContextPullRequests.Add(new WorkspaceRepositoryContextPullRequest
            {
                WorkspaceFeatureContextId = contextId,
                WorkspaceRepositoryId = pr.WorkspaceRepositoryId,
                PullRequestNumber = pr.PullRequestNumber,
                State = pr.State,
                Mergeable = pr.Mergeable,
                MergeableState = pr.MergeableState,
                HtmlUrl = pr.HtmlUrl,
                MergedAt = pr.MergedAt,
                ChangedFiles = pr.ChangedFiles,
                LastCheckedAt = pr.LastCheckedAt
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private static async Task BackfillContextActionsAsync(AppDbContext dbContext, int contextId, int workspaceId)
    {
        var rows = await (
            from a in dbContext.WorkspaceRepositoryActions.AsNoTracking()
            join link in dbContext.WorkspaceRepositories.AsNoTracking() on a.WorkspaceRepositoryId equals link.WorkspaceRepositoryId
            where link.WorkspaceId == workspaceId
            select a).ToListAsync();

        foreach (var a in rows)
        {
            var exists = await dbContext.WorkspaceRepositoryContextActions
                .AnyAsync(x => x.WorkspaceFeatureContextId == contextId && x.WorkspaceRepositoryId == a.WorkspaceRepositoryId);
            if (exists)
                continue;

            dbContext.WorkspaceRepositoryContextActions.Add(new WorkspaceRepositoryContextAction
            {
                WorkspaceFeatureContextId = contextId,
                WorkspaceRepositoryId = a.WorkspaceRepositoryId,
                Status = a.Status,
                HtmlUrl = a.HtmlUrl,
                UpdatedAt = a.UpdatedAt,
                BranchName = a.BranchName,
                RunId = a.RunId,
                WorkflowId = a.WorkflowId,
                WorkflowName = a.WorkflowName,
                WorkflowsJson = a.WorkflowsJson,
                LastCheckedAt = a.LastCheckedAt
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private static async Task BackfillContextGitChangesAsync(AppDbContext dbContext, int contextId, int workspaceId)
    {
        var statuses = await (
            from s in dbContext.WorkspaceGitRepositoryStatuses.AsNoTracking()
            join link in dbContext.WorkspaceRepositories.AsNoTracking() on s.WorkspaceRepositoryId equals link.WorkspaceRepositoryId
            where link.WorkspaceId == workspaceId
            select s).ToListAsync();

        foreach (var s in statuses)
        {
            var exists = await dbContext.WorkspaceGitContextRepositoryStatuses
                .AnyAsync(x => x.WorkspaceFeatureContextId == contextId && x.WorkspaceRepositoryId == s.WorkspaceRepositoryId);
            if (!exists)
            {
                dbContext.WorkspaceGitContextRepositoryStatuses.Add(new WorkspaceGitContextRepositoryStatus
                {
                    WorkspaceFeatureContextId = contextId,
                    WorkspaceRepositoryId = s.WorkspaceRepositoryId,
                    SnapshotVersion = s.SnapshotVersion,
                    BranchName = s.BranchName,
                    HeadCommit = s.HeadCommit,
                    IsDetachedHead = s.IsDetachedHead,
                    IsUnbornBranch = s.IsUnbornBranch,
                    IsMerging = s.IsMerging,
                    IsRebasing = s.IsRebasing,
                    IsCherryPicking = s.IsCherryPicking,
                    StagedCount = s.StagedCount,
                    ChangedCount = s.ChangedCount,
                    ConflictCount = s.ConflictCount,
                    Insertions = s.Insertions,
                    Deletions = s.Deletions,
                    StagedInsertions = s.StagedInsertions,
                    StagedDeletions = s.StagedDeletions,
                    AgentScannedAt = s.AgentScannedAt,
                    PersistedAt = s.PersistedAt,
                    LastErrorCode = s.LastErrorCode,
                    LastErrorMessage = s.LastErrorMessage
                });
            }

            var entryExists = await dbContext.WorkspaceGitContextChangeEntries
                .AnyAsync(e => e.WorkspaceFeatureContextId == contextId && e.WorkspaceRepositoryId == s.WorkspaceRepositoryId);
            if (entryExists)
                continue;

            var entries = await dbContext.WorkspaceGitChangeEntries.AsNoTracking()
                .Where(e => e.WorkspaceRepositoryId == s.WorkspaceRepositoryId)
                .ToListAsync();
            foreach (var e in entries)
            {
                dbContext.WorkspaceGitContextChangeEntries.Add(new WorkspaceGitContextChangeEntry
                {
                    WorkspaceFeatureContextId = contextId,
                    WorkspaceRepositoryId = e.WorkspaceRepositoryId,
                    Path = e.Path,
                    OriginalPath = e.OriginalPath,
                    IndexChange = e.IndexChange,
                    WorktreeChange = e.WorktreeChange,
                    IsTracked = e.IsTracked,
                    IsConflicted = e.IsConflicted,
                    IsSubmodule = e.IsSubmodule
                });
            }
        }

        await dbContext.SaveChangesAsync();
    }

    private static async Task AddNullableTextColumnIfMissingAsync(DbConnection conn, string tableName, string columnName)
    {
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = '{columnName}'";
        if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0)
            return;

        await using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} TEXT NULL";
        await alterCmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteNonQueryAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
