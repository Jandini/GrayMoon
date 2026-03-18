using GrayMoon.App.Models;
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
    IOptions<WorkspaceOptions> workspaceOptions)
{
    private readonly int _maxConcurrent = Math.Max(1, workspaceOptions?.Value?.MaxParallelOperations ?? 8);

    /// <summary>
    /// Runs the full update flow: refresh projects, sync and commit csproj deps (per level or single-level),
    /// refresh repo versions, then run version-file updates and optionally commit them.
    /// Stops on first error and reports it via <paramref name="onRepoError"/>.
    /// </summary>
    /// <param name="repoIdsToUpdate">Optional. When set, only these repositories are considered for the update plan and all steps.</param>
    /// <param name="onVersionFilesUpdated">
    /// Optional. When set and version files are updated, the orchestrator does not commit them but instead
    /// groups updated file paths per repo and invokes this callback so the caller (typically UI) can confirm
    /// and commit via <see cref="WorkspaceGitService.CommitFilePathsAsync"/>.
    /// When null, version-file commits are performed automatically.
    /// </param>
    public async Task RunAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        Action<string> setProgress,
        Action<int, string> onRepoError,
        Action? onAppSideComplete = null,
        IReadOnlySet<int>? repoIdsToUpdate = null,
        Action<IReadOnlyList<(int RepoId, string RepoName, IReadOnlyList<string> FilePaths)>>? onVersionFilesUpdated = null)
    {
        var hadError = false;
        void OnRepoError(int repoId, string msg)
        {
            hadError = true;
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
            return;

        // Step 2: Build update plan (repos with mismatched deps, by level).
        var (payload, isMultiLevel) = await workspaceGitService.GetUpdatePlanAsync(workspaceId, repoIdsToUpdate, cancellationToken);

        if (payload.Count > 0 && isMultiLevel)
        {
            // Multi-level: for each level, sync → commit → refresh version (commits required before next level).
            var levelsAsc = payload.Select(p => p.DependencyLevel ?? 0).Distinct().OrderBy(x => x).ToList();
            foreach (var level in levelsAsc)
            {
                if (hadError)
                    break;

                var reposAtLevel = payload.Where(p => (p.DependencyLevel ?? 0) == level).ToList();
                if (reposAtLevel.Count == 0)
                    continue;

                var repoIds = reposAtLevel.Select(r => r.RepoId).ToHashSet();

                // Step 3a: Sync .csproj files for this level.
                setProgress($"Updating {reposAtLevel.Count} {(reposAtLevel.Count == 1 ? "repository" : "repositories")}...");
                await workspaceGitService.SyncDependenciesAsync(
                    workspaceId,
                    onProgress: (c, t, _) => setProgress($"Syncing {c} of {t}"),
                    onRepoError: OnRepoError,
                    repoIdsToSync: repoIds,
                    cancellationToken);
                if (hadError)
                    break;

                // Step 4a: Commit .csproj changes for this level (required before next level).
                setProgress("Committing...");
                var commitResults = await workspaceGitService.CommitDependencyUpdatesAsync(
                    workspaceId,
                    reposAtLevel,
                    onProgress: (c, t, _) =>
                    {
                        setProgress($"Committed {c} of {t}");
                        if (c == t)
                            onAppSideComplete?.Invoke();
                    },
                    cancellationToken);
                foreach (var (repoId, errMsg) in commitResults)
                {
                    if (!string.IsNullOrEmpty(errMsg))
                    {
                        OnRepoError(repoId, errMsg);
                        break;
                    }
                }
                if (hadError)
                    break;

                // Step 5a: Refresh repo version so grid is up to date for this level.
                var totalRefresh = reposAtLevel.Count;
                var completedRefresh = 0;
                using var refreshSemaphore = new SemaphoreSlim(_maxConcurrent);
                var refreshTasks = reposAtLevel.Select(async repo =>
                {
                    await refreshSemaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var (refreshSuccess, refreshError) = await workspaceGitService.SyncSingleRepositoryAsync(repo.RepoId, workspaceId, cancellationToken);
                        var c = Interlocked.Increment(ref completedRefresh);
                        setProgress($"Updating version {c} of {totalRefresh}...");
                        if (c == totalRefresh)
                            onAppSideComplete?.Invoke();
                        return (repo.RepoId, refreshSuccess, refreshError);
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
                        OnRepoError(repoId, err ?? "Refresh version failed.");
                }

                if (hadError)
                    break;
            }
        }
        else if (payload.Count > 0)
        {
            // Single-level: sync all → commit all → refresh version for all.
            setProgress("Syncing dependencies...");
            await workspaceGitService.SyncDependenciesAsync(
                workspaceId,
                onProgress: (c, t, _) => setProgress($"Synced dependencies {c} of {t}"),
                onRepoError: OnRepoError,
                repoIdsToSync: repoIdsToUpdate,
                cancellationToken);
            if (hadError)
                return;

            setProgress("Committing updates...");
            var commitResults = await workspaceGitService.CommitDependencyUpdatesAsync(
                workspaceId,
                payload,
                onProgress: (c, t, _) =>
                {
                    setProgress($"Committed {c} of {t}");
                    if (c == t)
                        onAppSideComplete?.Invoke();
                },
                cancellationToken);
            foreach (var (repoId, errMsg) in commitResults)
            {
                if (!string.IsNullOrEmpty(errMsg))
                {
                    OnRepoError(repoId, errMsg);
                    return;
                }
            }

            if (payload.Count > 0)
            {
                var totalRefresh = payload.Count;
                var completedRefresh = 0;
                using var refreshSemaphore = new SemaphoreSlim(_maxConcurrent);
                var refreshTasks = payload.Select(async repo =>
                {
                    await refreshSemaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var (refreshSuccess, refreshError) = await workspaceGitService.SyncSingleRepositoryAsync(repo.RepoId, workspaceId, cancellationToken);
                        var c = Interlocked.Increment(ref completedRefresh);
                        setProgress($"Updating version {c} of {totalRefresh}...");
                        if (c == totalRefresh)
                            onAppSideComplete?.Invoke();
                        return (repo.RepoId, refreshSuccess, refreshError);
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
                        OnRepoError(repoId, err ?? "Refresh version failed.");
                }
            }
        }

        // Step 3+: Always update configured version files after dependency updates.
        // This ensures version files aren't skipped when a dependency level has no csproj mismatches,
        // or when the update plan is empty (payload.Count == 0).
        if (!hadError)
        {
            setProgress("Updating version files...");
            HashSet<int>? selectedRepoIds = null;
            if (repoIdsToUpdate != null && repoIdsToUpdate.Count > 0)
                selectedRepoIds = new HashSet<int>(repoIdsToUpdate);

            var (_, _, fileError, updatedFiles) = await fileVersionService.UpdateAllVersionsAsync(
                workspaceId,
                selectedRepositoryIds: selectedRepoIds,
                onFileUpdated: null,
                cancellationToken: cancellationToken);

            if (fileError != null && !fileError.Contains("No version configurations", StringComparison.OrdinalIgnoreCase))
            {
                OnRepoError(0, fileError);
            }

            if (!hadError && updatedFiles is { Count: > 0 })
            {
                var byRepo = updatedFiles
                    .GroupBy(x => (x.RepositoryId, x.RepoName))
                    .Select(g => (g.Key.RepositoryId, g.Key.RepoName, (IReadOnlyList<string>)g.Select(x => x.FilePath).Distinct().ToList()))
                    .ToList();

                if (onVersionFilesUpdated != null)
                {
                    onVersionFilesUpdated(byRepo);
                }
                else
                {
                    setProgress("Committing updated versions...");
                    var vfCommitResults = await workspaceGitService.CommitFilePathsAsync(
                        workspaceId,
                        byRepo,
                        onProgress: (c, t, _) => setProgress($"Committed version files {c} of {t}"),
                        cancellationToken: cancellationToken);

                    foreach (var (repoId, errMsg) in vfCommitResults)
                    {
                        if (!string.IsNullOrEmpty(errMsg))
                            OnRepoError(repoId, errMsg);
                    }
                }
            }
        }

        // Finalize: broadcast so grid refreshes.
        if (!hadError)
        {
            onAppSideComplete?.Invoke();
            await workspaceGitService.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, cancellationToken);
        }
    }
}
