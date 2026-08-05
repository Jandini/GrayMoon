using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services;

/// <summary>
/// Runs the dependency-update workflow: refresh projects, build plan, then per level (or single-level):
/// sync .csproj files, commit changes, refresh repo version. Commits are required before moving to the next level.
/// Stateless; no UI types. Caller provides progress and error callbacks.
/// </summary>
public sealed class DependencyUpdateOrchestrator(
    WorkspaceGitService workspaceGitService,
    WorkspaceFileVersionService fileVersionService,
    WorkspaceRepository workspaceRepository,
    IOptions<WorkspaceOptions> workspaceOptions,
    IServiceScopeFactory scopeFactory,
    ILogger<DependencyUpdateOrchestrator> logger)
{
    private readonly int _maxConcurrent = Math.Max(1, workspaceOptions?.Value?.MaxParallelOperations ?? 8);

    /// <summary>
    /// Runs the full update flow per dependency level:
    /// refresh projects, update+commit version files, sync+commit csproj deps, refresh repo versions.
    /// Walks all dependency levels (up to <paramref name="maxLevel"/>) so version-file work that appears
    /// after a lower-level commit is not skipped. Csproj sync is scoped to <paramref name="repoIdsToUpdate"/>.
    /// Stops on first error and reports it via <paramref name="onRepoError"/>.
    /// </summary>
    /// <param name="repoIdsToUpdate">Optional. When set, only these repositories are considered for the update plan and all steps.</param>
    /// <param name="commitMessage">Optional user-supplied commit subject line. When provided, replaces the default subject in all commits created during this update.</param>
    /// <param name="includeDepsInCommitMessage">When true, the list of updated packages is appended to the commit message body.</param>
    /// <param name="maxLevel">Optional. When set, only repositories at or below this dependency level are processed; higher levels are skipped.</param>
    /// <param name="runId">Optional caller-supplied correlation id included in every log line for this run so it can be filtered from application logs.</param>
    public async Task<IReadOnlySet<int>> RunAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        Action<string> setProgress,
        Action<int, string> onRepoError,
        Action? onAppSideComplete = null,
        IReadOnlySet<int>? repoIdsToUpdate = null,
        string? commitMessage = null,
        bool includeDepsInCommitMessage = true,
        int? maxLevel = null,
        string? runId = null)
    {
        // Non-null empty set means the caller determined no repos need work.
        if (repoIdsToUpdate is { Count: 0 })
        {
            logger.LogInformation("[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: caller passed an empty repoIdsToUpdate set; nothing to do.", runId, workspaceId);
            return new HashSet<int>();
        }

        logger.LogInformation(
            "[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: starting update run. Scope={Scope}, MaxLevel={MaxLevel}",
            runId, workspaceId,
            repoIdsToUpdate == null ? "all repos (live, per level)" : $"{repoIdsToUpdate.Count} repo(s): [{string.Join(",", repoIdsToUpdate)}]",
            maxLevel?.ToString() ?? "none");

        var hadError = false;
        var allSyncedRepoIds = new HashSet<int>();
        void OnRepoError(int repoId, string msg)
        {
            hadError = true;
            logger.LogWarning("[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: repo {RepoId} error: {Message}", runId, workspaceId, repoId, msg);
            onRepoError(repoId, msg);
        }

        // Step 1: Refresh project data from .csproj files on disk.
        setProgress("Reading project files...");
        await workspaceGitService.RefreshWorkspaceProjectsAsync(
            workspaceId,
            onProgress: (c, t, _) => setProgress($"Read {c} of {t} project files"),
            onRepoError: OnRepoError,
            repositoryIds: repoIdsToUpdate,
            cancellationToken);
        if (hadError)
        {
            logger.LogWarning("[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: aborting after RefreshWorkspaceProjectsAsync error.", runId, workspaceId);
            return new HashSet<int>();
        }

        // Step 2+: Walk every dependency level (up to maxLevel). Do not limit the level walk to the
        // initial reposNeedingWork set - a lower-level csproj commit refreshes GitVersion and can mark
        // higher-level version files out of date; those repos must still be visited for file updates.
        var levelRepoIds = await GetRepositoryIdsByDependencyLevelAsync(workspaceId, selectedRepositoryIds: null, OnRepoError);
        if (maxLevel.HasValue)
            levelRepoIds = levelRepoIds.Where(x => x.Level <= maxLevel.Value).ToList();
        if (levelRepoIds.Count == 0)
            hadError = true;
        if (hadError)
            return new HashSet<int>();

        logger.LogInformation(
            "[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: {LevelCount} level(s) to walk: {Levels}",
            runId, workspaceId, levelRepoIds.Count,
            string.Join(", ", levelRepoIds.Select(l => $"L{l.Level}={l.RepoIds.Count} repo(s)")));

        foreach (var (level, repoIds) in levelRepoIds)
        {
            if (hadError)
                break;

            if (repoIds.Count == 0)
                continue;

            // Re-fetch per level: RefreshRepositoryVersionsAsync updates OutOfDateFileRepos in the DB
            // after each level commits, so re-reading here ensures newly out-of-date files at higher
            // levels are not skipped.
            var freshWorkspace = await workspaceRepository.GetByIdAsync(workspaceId);
            var outOfDateFileRepoIds = (freshWorkspace?.Repositories ?? (ICollection<WorkspaceRepositoryLink>)[])
                .Where(l => (l.OutOfDateFileRepos ?? 0) > 0)
                .Select(l => l.RepositoryId)
                .ToHashSet();

            // Csproj sync stays scoped to the caller's selection (or all repos at this level when null).
            var csprojScope = repoIdsToUpdate != null
                ? (IReadOnlySet<int>)repoIds.Where(repoIdsToUpdate.Contains).ToHashSet()
                : repoIds;

            var hasFileWork = repoIds.Any(outOfDateFileRepoIds.Contains);

            if (repoIdsToUpdate != null)
            {
                var excludedByScope = repoIds.Where(id => !repoIdsToUpdate.Contains(id)).ToList();
                if (excludedByScope.Count > 0)
                {
                    logger.LogInformation(
                        "[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: {ExcludedCount} repo(s) excluded from csproj scope by caller's repoIdsToUpdate: [{ExcludedIds}]",
                        runId, workspaceId, level, excludedByScope.Count, string.Join(",", excludedByScope));
                }
            }

            logger.LogInformation(
                "[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: starting. TotalRepos={TotalRepos}, CsprojScope={CsprojScope}, HasFileWork={HasFileWork}",
                runId, workspaceId, level, repoIds.Count, csprojScope.Count, hasFileWork);

            if (!hasFileWork && csprojScope.Count == 0)
            {
                logger.LogInformation("[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: skipped (no file work, empty csproj scope).", runId, workspaceId, level);
                continue;
            }

            Action<string> levelProgress = msg => setProgress($"{msg}\nLevel {level}");

            // Version files must be committed first because those commits can change
            // versions consumed by dependency updates at this and higher levels.
            (bool vfOk, IReadOnlyList<int> vfCommittedRepoIds) = await UpdateAndCommitVersionFilesAsync(
                workspaceId,
                repoIds,
                outOfDateFileRepoIds,
                level,
                cancellationToken,
                levelProgress,
                onAppSideComplete,
                OnRepoError,
                commitMessage,
                runId);
            if (!vfOk)
            {
                hadError = true;
                break;
            }

            logger.LogInformation(
                "[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: version-file commit result: {CommittedCount} repo(s) committed: [{CommittedIds}]",
                runId, workspaceId, level, vfCommittedRepoIds.Count, string.Join(",", vfCommittedRepoIds));

            if (csprojScope.Count == 0)
            {
                if (vfCommittedRepoIds.Count > 0
                    && !await RefreshRepositoryVersionsAsync(vfCommittedRepoIds, workspaceId, cancellationToken, levelProgress, onAppSideComplete, OnRepoError))
                {
                    hadError = true;
                }
                continue;
            }

            var (payload, _) = await workspaceGitService.GetUpdatePlanAsync(workspaceId, csprojScope, cancellationToken);
            var reposAtLevel = payload
                .Where(p => csprojScope.Contains(p.RepoId))
                .ToList();

            logger.LogInformation(
                "[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: GetUpdatePlanAsync found {PendingCount} of {ScopeCount} scoped repo(s) with pending csproj changes: [{PendingIds}]",
                runId, workspaceId, level, reposAtLevel.Count, csprojScope.Count, string.Join(",", reposAtLevel.Select(r => r.RepoId)));

            if (reposAtLevel.Count == 0)
            {
                // No csproj work at this level, but version-file commits may need a version refresh.
                if (vfCommittedRepoIds.Count > 0
                    && !await RefreshRepositoryVersionsAsync(vfCommittedRepoIds, workspaceId, cancellationToken, levelProgress, onAppSideComplete, OnRepoError))
                {
                    hadError = true;
                }
                continue;
            }

            levelProgress($"Updating {reposAtLevel.Count} {(reposAtLevel.Count == 1 ? "repository" : "repositories")}...");
            var syncedRepoIds = await workspaceGitService.SyncDependenciesAsync(
                workspaceId,
                onProgress: (c, t, _) => levelProgress($"Syncing {c} of {t}"),
                onRepoError: OnRepoError,
                repoIdsToSync: csprojScope,
                cancellationToken);
            allSyncedRepoIds.UnionWith(syncedRepoIds);

            logger.LogInformation(
                "[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: SyncDependenciesAsync synced {SyncedCount} of {AttemptedCount} repo(s): [{SyncedIds}]",
                runId, workspaceId, level, syncedRepoIds.Count, reposAtLevel.Count, string.Join(",", syncedRepoIds));

            if (hadError)
                break;

            var reposToCommit = reposAtLevel.Where(r => syncedRepoIds.Contains(r.RepoId)).ToList();
            if (reposToCommit.Count == 0)
            {
                if (vfCommittedRepoIds.Count > 0
                    && !await RefreshRepositoryVersionsAsync(vfCommittedRepoIds, workspaceId, cancellationToken, levelProgress, onAppSideComplete, OnRepoError))
                {
                    hadError = true;
                }
                continue;
            }

            levelProgress("Committing...");
            var commitResults = await workspaceGitService.CommitDependencyUpdatesAsync(
                workspaceId,
                reposToCommit,
                onProgress: (c, t, _) =>
                {
                    levelProgress($"Committed {c} of {t}");
                    if (c == t)
                        onAppSideComplete?.Invoke();
                },
                cancellationToken,
                commitMessageOverride: commitMessage,
                includeDepsInCommitMessage: includeDepsInCommitMessage);

            var csprojCommittedRepoIds = new List<int>();
            foreach (var (repoId, committed, errMsg) in commitResults)
            {
                if (!string.IsNullOrEmpty(errMsg))
                {
                    OnRepoError(repoId, errMsg);
                    break;
                }
                if (committed)
                    csprojCommittedRepoIds.Add(repoId);
            }

            logger.LogInformation(
                "[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: committed csproj changes for {CommittedCount} repo(s): [{CommittedIds}]",
                runId, workspaceId, level, csprojCommittedRepoIds.Count, string.Join(",", csprojCommittedRepoIds));

            if (hadError)
                break;

            var toRefresh = csprojCommittedRepoIds
                .Union(vfCommittedRepoIds)
                .ToList();
            if (toRefresh.Count > 0
                && !await RefreshRepositoryVersionsAsync(toRefresh, workspaceId, cancellationToken, levelProgress, onAppSideComplete, OnRepoError))
            {
                hadError = true;
                break;
            }

            logger.LogInformation("[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: completed.", runId, workspaceId, level);
        }

        // Finalize: broadcast so grid refreshes, then run one file-version check for the whole update.
        if (!hadError)
        {
            onAppSideComplete?.Invoke();
            await workspaceGitService.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
            await fileVersionService.CheckAndPersistFileVersionStatusAsync(workspaceId, cancellationToken);
        }

        logger.LogInformation(
            "[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: run finished. HadError={HadError}, TotalSyncedRepos={SyncedCount}",
            runId, workspaceId, hadError, hadError ? 0 : allSyncedRepoIds.Count);

        return hadError ? new HashSet<int>() : allSyncedRepoIds;
    }

    private async Task<IReadOnlyList<(int Level, IReadOnlySet<int> RepoIds)>> GetRepositoryIdsByDependencyLevelAsync(
        int workspaceId,
        IReadOnlySet<int>? selectedRepositoryIds,
        Action<int, string> onRepoError)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
        {
            onRepoError(0, $"Workspace {workspaceId} not found.");
            return [];
        }

        // Repositories pinned to a tag (detached HEAD) must never participate in update: sync/commit/version refresh corrupts persisted branch/tag state and can yield "(no branch)".
        bool IsPinnedToTag(WorkspaceRepositoryLink link) =>
            !string.IsNullOrWhiteSpace(link.CheckedOutTag);

        var levelRepoIds = workspace.Repositories
            .Where(link => link.Repository != null)
            .Where(link => !IsPinnedToTag(link))
            .Where(link => selectedRepositoryIds == null || selectedRepositoryIds.Contains(link.RepositoryId))
            .GroupBy(link => link.DependencyLevel ?? 0)
            .OrderBy(g => g.Key)
            .Select(g => (Level: g.Key, RepoIds: (IReadOnlySet<int>)g.Select(x => x.RepositoryId).ToHashSet()))
            .ToList();

        if (levelRepoIds.Count > 0)
            return levelRepoIds;

        // Do not fall back to raw selectedRepositoryIds: tag-pinned IDs would incorrectly be scheduled here after the filter yields no groups.
        if (selectedRepositoryIds is { Count: > 0 })
        {
            var nonTaggedFromSelection = workspace.Repositories
                .Where(link => link.Repository != null && selectedRepositoryIds.Contains(link.RepositoryId))
                .Where(link => !IsPinnedToTag(link))
                .Select(link => link.RepositoryId)
                .ToHashSet();
            if (nonTaggedFromSelection.Count > 0)
                return [(0, nonTaggedFromSelection)];
        }

        onRepoError(0, "No repositories found for update.");
        return [];
    }

    private async Task<bool> RefreshRepositoryVersionsAsync(
        IReadOnlyList<int> repositoryIds,
        int workspaceId,
        CancellationToken cancellationToken,
        Action<string> setProgress,
        Action? onAppSideComplete,
        Action<int, string> onRepoError)
    {
        if (repositoryIds.Count == 0)
            return true;

        var totalRefresh = repositoryIds.Count;
        var completedRefresh = 0;
        using var refreshSemaphore = new SemaphoreSlim(_maxConcurrent);
        var refreshTasks = repositoryIds.Select(async repoId =>
        {
            await refreshSemaphore.WaitAsync(cancellationToken);
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                var (refreshSuccess, refreshError) = await svc.SyncSingleRepositoryAsync(repoId, workspaceId, cancellationToken);
                var c = Interlocked.Increment(ref completedRefresh);
                setProgress($"Updating version {c} of {totalRefresh}...");
                return (RepoId: repoId, Success: refreshSuccess, Error: refreshError);
            }
            finally
            {
                refreshSemaphore.Release();
            }
        });

        var refreshResults = await Task.WhenAll(refreshTasks);
        foreach (var (repoId, success, err) in refreshResults)
        {
            if (!success)
            {
                onRepoError(repoId, err ?? "Refresh version failed.");
                return false;
            }
        }

        onAppSideComplete?.Invoke();
        return true;
    }

    /// <summary>
    /// Returns (success, committedRepoIds). committedRepoIds contains repo IDs where a version-file commit was actually created.
    /// </summary>
    private async Task<(bool Success, IReadOnlyList<int> CommittedRepoIds)> UpdateAndCommitVersionFilesAsync(
        int workspaceId,
        IReadOnlySet<int> selectedRepositoryIds,
        IReadOnlySet<int> outOfDateFileRepoIds,
        int level,
        CancellationToken cancellationToken,
        Action<string> setProgress,
        Action? onAppSideComplete,
        Action<int, string> onRepoError,
        string? commitMessageOverride = null,
        string? runId = null)
    {
        // Only call agent for repos that actually have out-of-date version files.
        var fileRepoIds = outOfDateFileRepoIds.Count > 0
            ? (IReadOnlySet<int>)selectedRepositoryIds.Intersect(outOfDateFileRepoIds).ToHashSet()
            : (IReadOnlySet<int>)new HashSet<int>();

        if (fileRepoIds.Count == 0)
            return (true, []);

        logger.LogInformation(
            "[UpdateOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: {FileRepoCount} repo(s) have out-of-date version files: [{FileRepoIds}]",
            runId, workspaceId, level, fileRepoIds.Count, string.Join(",", fileRepoIds));

        setProgress("Updating version files...");
        var (_, _, fileError, updatedFiles) = await fileVersionService.UpdateAllVersionsAsync(
            workspaceId,
            selectedRepositoryIds: fileRepoIds,
            filterPatternTokensToSelectedRepositories: false,
            onFileUpdated: null,
            cancellationToken: cancellationToken);

        if (fileError != null && !fileError.Contains("No version configurations", StringComparison.OrdinalIgnoreCase))
        {
            onRepoError(0, fileError);
            return (false, []);
        }

        if (updatedFiles is not { Count: > 0 })
            return (true, []);

        var byRepo = updatedFiles
            .GroupBy(x => (x.RepositoryId, x.RepoName))
            .Select(g => (g.Key.RepositoryId, g.Key.RepoName, (IReadOnlyList<string>)g.Select(x => x.FilePath).Distinct().ToList()))
            .ToList();

        // Never commit version-file changes for repos pinned to a tag.
        var workspacePins = await workspaceRepository.GetByIdAsync(workspaceId);
        var tagPinnedIds = workspacePins?.Repositories?
            .Where(l => !string.IsNullOrWhiteSpace(l.CheckedOutTag))
            .Select(l => l.RepositoryId)
            .ToHashSet() ?? new HashSet<int>();
        if (tagPinnedIds.Count > 0)
            byRepo = byRepo.Where(r => !tagPinnedIds.Contains(r.RepositoryId)).ToList();
        if (byRepo.Count == 0)
            return (true, []);

        setProgress("Committing updated versions...");
        var vfCommitResults = await workspaceGitService.CommitFilePathsAsync(
            workspaceId,
            byRepo,
            onProgress: (c, t, _) => setProgress($"Committed version files {c} of {t}"),
            cancellationToken: cancellationToken,
            commitMessageOverride: commitMessageOverride);

        var committedVersionRepoIds = new List<int>();
        foreach (var (repoId, committed, errMsg) in vfCommitResults)
        {
            if (!string.IsNullOrEmpty(errMsg))
            {
                onRepoError(repoId, errMsg);
                return (false, []);
            }
            if (committed)
                committedVersionRepoIds.Add(repoId);
        }

        return (true, committedVersionRepoIds);
    }

}
