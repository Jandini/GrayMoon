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
    public async Task RunAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        Action<string> setProgress,
        Action<int, string> onRepoError,
        Action? onAppSideComplete = null,
        IReadOnlySet<int>? repoIdsToUpdate = null)
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

        // Step 2+: Rebuild plan before each level and process the current lowest level atomically.
        while (!hadError)
        {
            var (payload, _) = await workspaceGitService.GetUpdatePlanAsync(workspaceId, repoIdsToUpdate, cancellationToken);
            if (payload.Count == 0)
                break;

            var level = payload.Min(p => p.DependencyLevel ?? 0);
            var reposAtLevel = payload
                .Where(p => (p.DependencyLevel ?? 0) == level)
                .ToList();
            if (reposAtLevel.Count == 0)
                break;

            var repoIds = reposAtLevel.Select(r => r.RepoId).ToHashSet();

            setProgress($"Updating {reposAtLevel.Count} {(reposAtLevel.Count == 1 ? "repository" : "repositories")}...");
            await workspaceGitService.SyncDependenciesAsync(
                workspaceId,
                onProgress: (c, t, _) => setProgress($"Syncing {c} of {t}"),
                onRepoError: OnRepoError,
                repoIdsToSync: repoIds,
                cancellationToken);
            if (hadError)
                break;

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

            if (!await RefreshRepositoryVersionsAsync(
                    reposAtLevel.Select(r => r.RepoId).ToList(),
                    workspaceId,
                    cancellationToken,
                    setProgress,
                    onAppSideComplete,
                    OnRepoError))
            {
                hadError = true;
                break;
            }

            setProgress("Updating version files...");
            var (_, _, fileError, updatedFiles) = await fileVersionService.UpdateAllVersionsAsync(
                workspaceId,
                selectedRepositoryIds: repoIds,
                onFileUpdated: null,
                cancellationToken: cancellationToken);

            if (fileError != null && !fileError.Contains("No version configurations", StringComparison.OrdinalIgnoreCase))
            {
                OnRepoError(0, fileError);
                break;
            }

            if (updatedFiles is { Count: > 0 })
            {
                var byRepo = updatedFiles
                    .GroupBy(x => (x.RepositoryId, x.RepoName))
                    .Select(g => (g.Key.RepositoryId, g.Key.RepoName, (IReadOnlyList<string>)g.Select(x => x.FilePath).Distinct().ToList()))
                    .ToList();

                setProgress("Committing updated versions...");
                var vfCommitResults = await workspaceGitService.CommitFilePathsAsync(
                    workspaceId,
                    byRepo,
                    onProgress: (c, t, _) => setProgress($"Committed version files {c} of {t}"),
                    cancellationToken: cancellationToken);

                var committedVersionRepoIds = new List<int>();
                foreach (var (repoId, errMsg) in vfCommitResults)
                {
                    if (!string.IsNullOrEmpty(errMsg))
                    {
                        OnRepoError(repoId, errMsg);
                        break;
                    }
                    committedVersionRepoIds.Add(repoId);
                }
                if (hadError)
                    break;

                // Version-file commits must also refresh GitVersion before planning next higher level.
                if (committedVersionRepoIds.Count > 0
                    && !await RefreshRepositoryVersionsAsync(
                        committedVersionRepoIds,
                        workspaceId,
                        cancellationToken,
                        setProgress,
                        onAppSideComplete,
                        OnRepoError))
                {
                    hadError = true;
                    break;
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
                var (refreshSuccess, refreshError) = await workspaceGitService.SyncSingleRepositoryAsync(repoId, workspaceId, cancellationToken);
                var c = Interlocked.Increment(ref completedRefresh);
                setProgress($"Updating version {c} of {totalRefresh}...");
                if (c == totalRefresh)
                    onAppSideComplete?.Invoke();
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

        return true;
    }
}
