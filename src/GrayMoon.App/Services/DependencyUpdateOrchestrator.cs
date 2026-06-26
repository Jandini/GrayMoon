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
    IServiceScopeFactory scopeFactory)
{
    private readonly int _maxConcurrent = Math.Max(1, workspaceOptions?.Value?.MaxParallelOperations ?? 8);

    /// <summary>
    /// Runs the full update flow per dependency level:
    /// refresh projects, update+commit version files, sync+commit csproj deps, refresh repo versions.
    /// Stops on first error and reports it via <paramref name="onRepoError"/>.
    /// </summary>
    /// <param name="repoIdsToUpdate">Optional. When set, only these repositories are considered for the update plan and all steps.</param>
    /// <param name="commitMessage">Optional user-supplied commit subject line. When provided, replaces the default subject in all commits created during this update.</param>
    /// <param name="includeDepsInCommitMessage">When true, the list of updated packages is appended to the commit message body.</param>
    /// <param name="maxLevel">Optional. When set, only repositories at or below this dependency level are processed; higher levels are skipped.</param>
    public async Task RunAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        Action<string> setProgress,
        Action<int, string> onRepoError,
        Action? onAppSideComplete = null,
        IReadOnlySet<int>? repoIdsToUpdate = null,
        string? commitMessage = null,
        bool includeDepsInCommitMessage = true,
        int? maxLevel = null)
    {
        // Non-null empty set means the caller determined no repos need work.
        if (repoIdsToUpdate is { Count: 0 })
            return;

        var hadError = false;
        void OnRepoError(int repoId, string msg)
        {
            hadError = true;
            onRepoError(repoId, msg);
        }

        // Load workspace links once: used to scope version-file work to repos with out-of-date files.
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        var workspaceLinks = workspace?.Repositories ?? (ICollection<WorkspaceRepositoryLink>)[];
        var outOfDateFileRepoIds = workspaceLinks
            .Where(l => (l.OutOfDateFileRepos ?? 0) > 0)
            .Select(l => l.RepositoryId)
            .ToHashSet();

        // Step 1: Refresh project data from .csproj files on disk.
        setProgress("Reading project files...");
        await workspaceGitService.RefreshWorkspaceProjectsAsync(
            workspaceId,
            onProgress: (c, t, _) => setProgress($"Read {c} of {t} project files"),
            onRepoError: OnRepoError,
            repositoryIds: repoIdsToUpdate,
            cancellationToken);
        if (hadError)
            return;

        // Step 2+: Process repositories by dependency level.
        var levelRepoIds = await GetRepositoryIdsByDependencyLevelAsync(workspaceId, repoIdsToUpdate, OnRepoError);
        if (maxLevel.HasValue)
            levelRepoIds = levelRepoIds.Where(x => x.Level <= maxLevel.Value).ToList();
        if (levelRepoIds.Count == 0)
            hadError = true;

        foreach (var (level, repoIds) in levelRepoIds)
        {
            if (hadError)
                break;

            if (repoIds.Count == 0)
                break;

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
                commitMessage);
            if (!vfOk)
            {
                hadError = true;
                break;
            }

            var (payload, _) = await workspaceGitService.GetUpdatePlanAsync(workspaceId, repoIds, cancellationToken);
            var reposAtLevel = payload
                .Where(p => repoIds.Contains(p.RepoId))
                .ToList();
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
                repoIdsToSync: repoIds,
                cancellationToken);
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
        }

        // Finalize: broadcast so grid refreshes, then run one file-version check for the whole update.
        if (!hadError)
        {
            onAppSideComplete?.Invoke();
            await workspaceGitService.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
            await fileVersionService.CheckAndPersistFileVersionStatusAsync(workspaceId, cancellationToken);
        }
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
        string? commitMessageOverride = null)
    {
        // Only call agent for repos that actually have out-of-date version files.
        var fileRepoIds = outOfDateFileRepoIds.Count > 0
            ? (IReadOnlySet<int>)selectedRepositoryIds.Intersect(outOfDateFileRepoIds).ToHashSet()
            : (IReadOnlySet<int>)new HashSet<int>();

        if (fileRepoIds.Count == 0)
            return (true, []);

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
