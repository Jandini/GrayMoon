using System.Data.Common;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App;

public static partial class Migrations
{
    /// <summary>
    /// Additive WorkspaceFeatureContext schema + special-Workspace backfill for existing databases.
    /// EnsureCreated() covers brand-new databases from the current model; this patches older files.
    /// </summary>
    public static async Task MigrateWorkspaceFeatureContextSchemaAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        try
        {
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await AddNullableTextColumnIfMissingAsync(conn, "Workspaces", "ManagedFeatureStorageRoot");

            await EnsureFeatureCoreTablesAsync(conn);
            await AddNullableTextColumnIfMissingAsync(conn, "WorkspaceFeatureRepositories", "ParentBranchName");
            await AddNullableTextColumnIfMissingAsync(conn, "WorkspaceFeatureRepositories", "PinnedTag");
            await EnsureFeatureProjectionTablesAsync(conn);
            await EnsureProjectAndFileLineContextColumnsAsync(conn);
            await BackfillSpecialWorkspaceContextsAsync(dbContext);
        }
        catch (Exception ex)
        {
            // Fresh DB: EnsureCreated already created the Feature tables from the model.
            logger.LogError(ex, "Legacy migration step MigrateWorkspaceFeatureContextSchemaAsync failed; continuing startup.");
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
                "ParentBranchName" TEXT NULL,
                "PinnedTag" TEXT NULL,
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
                "GitVersionPending" INTEGER NULL,
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

        // Prefer context-scoped uniqueness when the column exists; drop leftover workspace-wide unique indexes.
        await DropUniqueIndexesMatchingColumnsAsync(conn, "WorkspaceProjects", "WorkspaceId", "RepositoryId", "ProjectName");
        await DropUniqueIndexesMatchingColumnsAsync(conn, "WorkspaceFileLineStatuses", "WorkspaceId", "RepositoryId", "FilePath", "TokenName");

        await ExecuteNonQueryAsync(conn, """
            CREATE INDEX IF NOT EXISTS "IX_WorkspaceProjects_Workspace_Repo_Name"
            ON "WorkspaceProjects" ("WorkspaceId", "RepositoryId", "ProjectName");
            """);

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

            var existingStateRepoIds = new HashSet<int>(await dbContext.WorkspaceRepositoryContextStates
                .AsNoTracking()
                .Where(s => s.WorkspaceFeatureContextId == context.WorkspaceFeatureContextId)
                .Select(s => s.WorkspaceRepositoryId)
                .ToListAsync());

            foreach (var link in links)
            {
                if (existingStateRepoIds.Contains(link.WorkspaceRepositoryId))
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

        var existingFileIds = new HashSet<int>(await dbContext.WorkspaceFileContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == contextId)
            .Select(s => s.FileId)
            .ToListAsync());

        foreach (var file in files)
        {
            if (existingFileIds.Contains(file.FileId))
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

        var existingRepoIds = new HashSet<int>(await dbContext.WorkspaceRepositoryContextPullRequests
            .AsNoTracking()
            .Where(x => x.WorkspaceFeatureContextId == contextId)
            .Select(x => x.WorkspaceRepositoryId)
            .ToListAsync());

        foreach (var pr in rows)
        {
            if (existingRepoIds.Contains(pr.WorkspaceRepositoryId))
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

        var existingRepoIds = new HashSet<int>(await dbContext.WorkspaceRepositoryContextActions
            .AsNoTracking()
            .Where(x => x.WorkspaceFeatureContextId == contextId)
            .Select(x => x.WorkspaceRepositoryId)
            .ToListAsync());

        foreach (var a in rows)
        {
            if (existingRepoIds.Contains(a.WorkspaceRepositoryId))
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

        var existingStatusRepoIds = new HashSet<int>(await dbContext.WorkspaceGitContextRepositoryStatuses
            .AsNoTracking()
            .Where(x => x.WorkspaceFeatureContextId == contextId)
            .Select(x => x.WorkspaceRepositoryId)
            .ToListAsync());

        var existingEntryRepoIds = new HashSet<int>(await dbContext.WorkspaceGitContextChangeEntries
            .AsNoTracking()
            .Where(e => e.WorkspaceFeatureContextId == contextId)
            .Select(e => e.WorkspaceRepositoryId)
            .ToListAsync());

        foreach (var s in statuses)
        {
            if (!existingStatusRepoIds.Contains(s.WorkspaceRepositoryId))
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
                    WorkerScannedAt = s.WorkerScannedAt,
                    PersistedAt = s.PersistedAt,
                    LastErrorCode = s.LastErrorCode,
                    LastErrorMessage = s.LastErrorMessage
                });
            }

            if (existingEntryRepoIds.Contains(s.WorkspaceRepositoryId))
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

    /// <summary>Adds the nullable GitVersionPending flag a Feature seed uses to mark versions not yet computed for the Feature's own checkout.</summary>
    public static async Task MigrateContextStateGitVersionPendingAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositoryContextStates') WHERE name = 'GitVersionPending'";
        if (Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0)
            return;

        await using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = "ALTER TABLE WorkspaceRepositoryContextStates ADD COLUMN GitVersionPending INTEGER NULL";
        await alterCmd.ExecuteNonQueryAsync();
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

    /// <summary>
    /// Drops leftover unique indexes whose columns match exactly. Feature contexts reuse the same
    /// WorkspaceId/RepositoryId/ProjectName (or file-line token) as Workspace, so uniqueness must be
    /// context-scoped. Table UNIQUE constraints (sqlite_autoindex_*) cannot be dropped this way.
    /// </summary>
    private static async Task DropUniqueIndexesMatchingColumnsAsync(
        DbConnection conn,
        string tableName,
        params string[] exactColumns)
    {
        var names = new List<string>();
        await using (var listCmd = conn.CreateCommand())
        {
            listCmd.CommandText = $"PRAGMA index_list('{tableName}')";
            await using var reader = await listCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var unique = reader.GetInt32(reader.GetOrdinal("unique")) != 0;
                var origin = reader.GetString(reader.GetOrdinal("origin"));
                var name = reader.GetString(reader.GetOrdinal("name"));
                if (!unique || origin != "c" || string.IsNullOrEmpty(name))
                    continue;
                names.Add(name);
            }
        }

        foreach (var name in names)
        {
            var columns = new List<string>();
            await using (var infoCmd = conn.CreateCommand())
            {
                infoCmd.CommandText = $"PRAGMA index_info('{name.Replace("'", "''")}')";
                await using var reader = await infoCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    columns.Add(reader.GetString(reader.GetOrdinal("name")));
            }

            if (columns.Count != exactColumns.Length)
                continue;
            if (!exactColumns.All(col => columns.Contains(col, StringComparer.OrdinalIgnoreCase)))
                continue;

            await ExecuteNonQueryAsync(conn, $"""DROP INDEX IF EXISTS "{name.Replace("\"", "\"\"")}" """);
        }
    }

    /// <summary>
    /// B2 strict step. Deletes rows left behind when a Feature (and its WorkspaceFeatureContexts row) was
    /// removed before the explicit project-data delete in B1 existed, then brings WorkspaceProjects up to the
    /// same WorkspaceFeatureContextId foreign key a fresh EnsureCreated() database has.
    ///
    /// WorkspaceFileLineStatuses gets orphan cleanup only, not a foreign key rebuild: that entity has no
    /// navigation property and no Fluent API configuration for WorkspaceFeatureContextId in AppDbContext at
    /// all, so a fresh database has no foreign key on that column either. Adding one here would not match a
    /// fresh database's shape, which is what the schema-parity test checks.
    /// </summary>
    public static async Task MigrateFeatureContextOrphanCleanupAndWorkspaceProjectsForeignKeyAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var deletedDependencies = await DeleteOrphanProjectDependenciesAsync(conn);
        var deletedProjects = await DeleteOrphanContextRowsAsync(conn, "WorkspaceProjects");
        var deletedFileLineStatuses = await DeleteOrphanContextRowsAsync(conn, "WorkspaceFileLineStatuses");

        logger.LogInformation(
            "Orphan cleanup removed {ProjectDependencyCount} ProjectDependencies, {WorkspaceProjectCount} " +
            "WorkspaceProjects and {WorkspaceFileLineStatusCount} WorkspaceFileLineStatuses rows whose " +
            "WorkspaceFeatureContextId no longer references an existing WorkspaceFeatureContexts row.",
            deletedDependencies, deletedProjects, deletedFileLineStatuses);

        await RebuildWorkspaceProjectsForeignKeyAsync(conn, logger);
    }

    /// <summary>
    /// Deletes rows of <paramref name="tableName"/> whose WorkspaceFeatureContextId is set but no longer
    /// matches a row in WorkspaceFeatureContexts. Rows with a null WorkspaceFeatureContextId are never touched.
    /// </summary>
    private static async Task<int> DeleteOrphanContextRowsAsync(DbConnection conn, string tableName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM "{tableName}"
            WHERE "WorkspaceFeatureContextId" IS NOT NULL
              AND "WorkspaceFeatureContextId" NOT IN (SELECT "WorkspaceFeatureContextId" FROM "WorkspaceFeatureContexts")
            """;
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// ProjectDependencies has no WorkspaceFeatureContextId column of its own; a dependency row is orphaned
    /// when either project it links is orphaned (see <see cref="DeleteOrphanContextRowsAsync"/>). Runs before
    /// the WorkspaceProjects cleanup so the orphan projects are still present to identify their dependencies.
    /// </summary>
    private static async Task<int> DeleteOrphanProjectDependenciesAsync(DbConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM "ProjectDependencies"
            WHERE "DependentProjectId" IN (
                    SELECT "ProjectId" FROM "WorkspaceProjects"
                    WHERE "WorkspaceFeatureContextId" IS NOT NULL
                      AND "WorkspaceFeatureContextId" NOT IN (SELECT "WorkspaceFeatureContextId" FROM "WorkspaceFeatureContexts")
                )
               OR "ReferencedProjectId" IN (
                    SELECT "ProjectId" FROM "WorkspaceProjects"
                    WHERE "WorkspaceFeatureContextId" IS NOT NULL
                      AND "WorkspaceFeatureContextId" NOT IN (SELECT "WorkspaceFeatureContextId" FROM "WorkspaceFeatureContexts")
                )
            """;
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Rebuilds WorkspaceProjects with the exact column list and foreign key constraints EF produces for a
    /// fresh EnsureCreated() database (captured by reading sqlite_master.sql for a fresh database in a test).
    /// SQLite cannot add a constraint to an existing table, so this creates a new table with the constraint,
    /// copies rows (keeping their Id, since ProjectDependencies references WorkspaceProjects by Id), drops the
    /// old table and renames the new one into place. A no-op once the foreign key already exists.
    ///
    /// ProjectDependencies has its own ON DELETE CASCADE foreign keys to WorkspaceProjects.ProjectId. SQLite
    /// fires those cascade actions for every row of the parent table when the parent table itself is dropped
    /// while the child's foreign key still targets it by name (this applies even inside a transaction, since
    /// SQLite's PRAGMA foreign_keys cannot be turned off while one is open, which Migrations.RunStrictStepAsync
    /// already has open by the time this method runs). Dropping WorkspaceProjects directly would silently wipe
    /// every ProjectDependencies row. To avoid that, ProjectDependencies is retargeted to the new table first
    /// (its own temporary rebuild, pointing at "WorkspaceProjects_new"), so no live foreign key still points at
    /// the old "WorkspaceProjects" table at the moment it is dropped; the final rename then lets SQLite's
    /// automatic foreign-key-text rewrite on RENAME point ProjectDependencies back at "WorkspaceProjects".
    /// </summary>
    private static async Task RebuildWorkspaceProjectsForeignKeyAsync(DbConnection conn, ILogger logger)
    {
        if (await HasForeignKeyToFeatureContextsAsync(conn, "WorkspaceProjects"))
            return;

        // Captured before the tables are dropped, so the same indexes can be recreated afterwards; DROP TABLE
        // removes every index defined on it.
        var workspaceProjectsIndexes = await ReadIndexDefinitionsAsync(conn, "WorkspaceProjects");
        var projectDependenciesIndexes = await ReadIndexDefinitionsAsync(conn, "ProjectDependencies");

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE "WorkspaceProjects_new" (
                "ProjectId" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceProjects" PRIMARY KEY AUTOINCREMENT,
                "WorkspaceId" INTEGER NOT NULL,
                "WorkspaceFeatureContextId" INTEGER NULL,
                "RepositoryId" INTEGER NOT NULL,
                "ProjectName" TEXT NOT NULL,
                "ProjectType" INTEGER NOT NULL,
                "ProjectFilePath" TEXT NOT NULL,
                "TargetFramework" TEXT NOT NULL,
                "PackageId" TEXT NULL,
                "IsGenerated" INTEGER NOT NULL,
                "MatchedConnectorId" INTEGER NULL,
                CONSTRAINT "FK_WorkspaceProjects_Connectors_MatchedConnectorId" FOREIGN KEY ("MatchedConnectorId") REFERENCES "Connectors" ("ConnectorId") ON DELETE SET NULL,
                CONSTRAINT "FK_WorkspaceProjects_Repositories_RepositoryId" FOREIGN KEY ("RepositoryId") REFERENCES "Repositories" ("RepositoryId") ON DELETE CASCADE,
                CONSTRAINT "FK_WorkspaceProjects_WorkspaceFeatureContexts_WorkspaceFeatureContextId" FOREIGN KEY ("WorkspaceFeatureContextId") REFERENCES "WorkspaceFeatureContexts" ("WorkspaceFeatureContextId") ON DELETE CASCADE,
                CONSTRAINT "FK_WorkspaceProjects_Workspaces_WorkspaceId" FOREIGN KEY ("WorkspaceId") REFERENCES "Workspaces" ("WorkspaceId") ON DELETE CASCADE
            );
            """);

        await ExecuteNonQueryAsync(conn, """
            INSERT INTO "WorkspaceProjects_new"
                ("ProjectId", "WorkspaceId", "WorkspaceFeatureContextId", "RepositoryId", "ProjectName", "ProjectType",
                 "ProjectFilePath", "TargetFramework", "PackageId", "IsGenerated", "MatchedConnectorId")
            SELECT "ProjectId", "WorkspaceId", "WorkspaceFeatureContextId", "RepositoryId", "ProjectName", "ProjectType",
                   "ProjectFilePath", "TargetFramework", "PackageId", "IsGenerated", "MatchedConnectorId"
            FROM "WorkspaceProjects";
            """);

        await AssertSameRowsAndIdsAsync(conn, "WorkspaceProjects", "WorkspaceProjects_new", "ProjectId");

        // Retarget ProjectDependencies at WorkspaceProjects_new before the old WorkspaceProjects table is
        // dropped, so SQLite finds no live foreign key pointing at it and does not cascade-delete these rows.
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE "ProjectDependencies_new" (
                "ProjectDependencyId" INTEGER NOT NULL CONSTRAINT "PK_ProjectDependencies" PRIMARY KEY AUTOINCREMENT,
                "DependentProjectId" INTEGER NOT NULL,
                "ReferencedProjectId" INTEGER NOT NULL,
                "Version" TEXT NULL,
                CONSTRAINT "FK_ProjectDependencies_WorkspaceProjects_DependentProjectId" FOREIGN KEY ("DependentProjectId") REFERENCES "WorkspaceProjects_new" ("ProjectId") ON DELETE CASCADE,
                CONSTRAINT "FK_ProjectDependencies_WorkspaceProjects_ReferencedProjectId" FOREIGN KEY ("ReferencedProjectId") REFERENCES "WorkspaceProjects_new" ("ProjectId") ON DELETE CASCADE
            );
            """);

        await ExecuteNonQueryAsync(conn, """
            INSERT INTO "ProjectDependencies_new" ("ProjectDependencyId", "DependentProjectId", "ReferencedProjectId", "Version")
            SELECT "ProjectDependencyId", "DependentProjectId", "ReferencedProjectId", "Version"
            FROM "ProjectDependencies";
            """);

        await AssertSameRowsAndIdsAsync(conn, "ProjectDependencies", "ProjectDependencies_new", "ProjectDependencyId");

        await ExecuteNonQueryAsync(conn, """DROP TABLE "ProjectDependencies";""");
        await ExecuteNonQueryAsync(conn, """ALTER TABLE "ProjectDependencies_new" RENAME TO "ProjectDependencies";""");

        // Safe now: the live ProjectDependencies table's foreign keys target "WorkspaceProjects_new", not this
        // table, so dropping it fires no cascade.
        await ExecuteNonQueryAsync(conn, """DROP TABLE "WorkspaceProjects";""");

        // SQLite rewrites other tables' foreign key definitions that reference the renamed table, so
        // ProjectDependencies ends up targeting "WorkspaceProjects" again automatically.
        await ExecuteNonQueryAsync(conn, """ALTER TABLE "WorkspaceProjects_new" RENAME TO "WorkspaceProjects";""");

        foreach (var indexSql in workspaceProjectsIndexes)
            await ExecuteNonQueryAsync(conn, indexSql);
        foreach (var indexSql in projectDependenciesIndexes)
            await ExecuteNonQueryAsync(conn, indexSql);

        await AssertNoForeignKeyViolationsAsync(conn, "WorkspaceProjects");
        await AssertNoForeignKeyViolationsAsync(conn, "ProjectDependencies");

        logger.LogInformation("Rebuilt WorkspaceProjects with the WorkspaceFeatureContextId foreign key to match a fresh database.");
    }

    private static async Task<List<string>> ReadIndexDefinitionsAsync(DbConnection conn, string tableName)
    {
        var definitions = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = '{tableName}' AND sql IS NOT NULL";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            definitions.Add(reader.GetString(0));
        return definitions;
    }

    private static async Task AssertSameRowsAndIdsAsync(DbConnection conn, string oldTableName, string newTableName, string idColumn)
    {
        var oldCount = await ScalarIntAsync(conn, $"SELECT COUNT(*) FROM \"{oldTableName}\"");
        var oldIdSum = await ScalarLongAsync(conn, $"SELECT COALESCE(SUM(\"{idColumn}\"), 0) FROM \"{oldTableName}\"");
        var newCount = await ScalarIntAsync(conn, $"SELECT COUNT(*) FROM \"{newTableName}\"");
        var newIdSum = await ScalarLongAsync(conn, $"SELECT COALESCE(SUM(\"{idColumn}\"), 0) FROM \"{newTableName}\"");
        if (oldCount != newCount || oldIdSum != newIdSum)
        {
            throw new InvalidOperationException(
                $"{oldTableName} rebuild row mismatch: old table has {oldCount} rows (id sum {oldIdSum}), " +
                $"new table has {newCount} rows (id sum {newIdSum}).");
        }
    }

    private static async Task AssertNoForeignKeyViolationsAsync(DbConnection conn, string tableName)
    {
        var violationCount = await ScalarIntAsync(conn, $"SELECT COUNT(*) FROM pragma_foreign_key_check('{tableName}')");
        if (violationCount > 0)
            throw new InvalidOperationException($"{tableName} rebuild left {violationCount} foreign key violation(s) behind.");
    }

    private static async Task<bool> HasForeignKeyToFeatureContextsAsync(DbConnection conn, string tableName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_foreign_key_list('{tableName}') WHERE \"table\" = 'WorkspaceFeatureContexts'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task<int> ScalarIntAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<long> ScalarLongAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// E1 strict step. Recreates "IX_WorkspaceFeatures_WorkspaceId_Name" with COLLATE NOCASE on Name, so
    /// 'Foo' and 'foo' count as the same Feature name on databases from before this change, matching a
    /// fresh EnsureCreated() database (<see cref="AppDbContext.ConfigureFeatureEntities"/>).
    ///
    /// Skipped (with a warning, never a startup failure) when the database already has a case-only
    /// duplicate: creating the new unique index would fail on that existing data, and this step must not
    /// block startup. The old, case-sensitive index is left in place in that case.
    /// </summary>
    public static async Task MigrateFeatureNameIndexCollationAsync(AppDbContext dbContext, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var caseOnlyDuplicateGroups = await CountCaseOnlyDuplicateFeatureNameGroupsAsync(conn);
        if (caseOnlyDuplicateGroups > 0)
        {
            logger.LogWarning(
                "Skipping Feature name case-insensitive index: {GroupCount} WorkspaceId/Name group(s) already " +
                "have a case-only duplicate. Keeping the existing case-sensitive index.",
                caseOnlyDuplicateGroups);
            return;
        }

        await ExecuteNonQueryAsync(conn, """DROP INDEX IF EXISTS "IX_WorkspaceFeatures_WorkspaceId_Name";""");
        await ExecuteNonQueryAsync(conn, """
            CREATE UNIQUE INDEX "IX_WorkspaceFeatures_WorkspaceId_Name"
            ON "WorkspaceFeatures" ("WorkspaceId", "Name" COLLATE NOCASE);
            """);

        logger.LogInformation("Recreated IX_WorkspaceFeatures_WorkspaceId_Name with COLLATE NOCASE on Name.");
    }

    private static async Task<int> CountCaseOnlyDuplicateFeatureNameGroupsAsync(DbConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM (
                SELECT "WorkspaceId", LOWER("Name") AS "NormalizedName"
                FROM "WorkspaceFeatures"
                GROUP BY "WorkspaceId", LOWER("Name")
                HAVING COUNT(*) > 1
            )
            """;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
