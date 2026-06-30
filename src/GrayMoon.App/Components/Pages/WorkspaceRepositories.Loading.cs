using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private async Task LoadWorkspaceAsync()
    {
        try
        {
            isLoading = true;
            errorMessage = null;
            await ReloadWorkspaceDataAsync();
            _ = RefreshPullRequestsInBackgroundAndReloadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Failed to load workspace. Please try again later.";
            workspaceRepositories = new List<WorkspaceRepositoryLink>();
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task ReloadWorkspaceDataAsync()
    {
        workspace = await WorkspacePageService.WorkspaceRepository.GetByIdAsync(WorkspaceId);
        if (workspace == null)
        {
            errorMessage = "Workspace not found.";
            workspaceRepositories = new List<WorkspaceRepositoryLink>();
            UpdateFilteredRepositories();
            return;
        }

        workspaceRepositories = workspace.Repositories
            .OrderByDescending(wr => wr.DependencyLevel ?? int.MinValue)
            .ThenBy(wr => GetRepositoryTypeSortOrder(wr.RepositoryType))
            .ThenByDescending(wr => wr.Dependencies ?? int.MinValue)
            .ToList();
        prByRepositoryId = BuildPrByRepositoryIdFromLinks(workspaceRepositories);
        await LoadMismatchedDependencyLinesAsync();
        UpdateFilteredRepositories();
    }

    private static int GetRepositoryTypeSortOrder(ProjectType? type) => type switch
    {
        ProjectType.Service    => 0,
        ProjectType.Package    => 1,
        ProjectType.Executable => 2,
        ProjectType.Library    => 3,
        ProjectType.Test       => 4,
        _                      => 5 // null - no projects yet
    };

    private static IReadOnlyDictionary<int, PullRequestInfo?> BuildPrByRepositoryIdFromLinks(List<WorkspaceRepositoryLink> links)
    {
        var dict = new Dictionary<int, PullRequestInfo?>();
        foreach (var wr in links.Where(wr => wr.PullRequest != null))
            dict[wr.RepositoryId] = wr.PullRequest!.PullRequestNumber.HasValue ? wr.PullRequest.ToPullRequestInfo() : null;
        return dict;
    }

    /// <summary>Optional: refresh PR from API then reload workspace data so grid shows updated badges. When a PR becomes merged, runs branch fetch for that repo (same as Switch Branch Fetch) so remote branch list is updated.</summary>
    private async Task RefreshPullRequestsInBackgroundAndReloadAsync()
    {
        var previouslyMergedRepoIds = prByRepositoryId
            .Where(kv => kv.Value?.IsMerged == true)
            .Select(kv => kv.Key)
            .ToHashSet();

        try
        {
            await WorkspacePageService.WorkspacePullRequestService.RefreshPullRequestsForWorkspaceAsync(WorkspaceId);
            await ReloadWorkspaceDataAsync();

            var newlyMergedRepoIds = prByRepositoryId
                .Where(kv => kv.Value?.IsMerged == true)
                .Select(kv => kv.Key)
                .Where(id => !previouslyMergedRepoIds.Contains(id))
                .ToList();

            foreach (var repositoryId in newlyMergedRepoIds)
            {
                await RefreshBranchesForRepositoryAsync(repositoryId);
            }

            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Background PR refresh failed for workspace {WorkspaceId}", WorkspaceId);
        }
    }

    /// <summary>Refreshes PR for one repository when user enters the PR badge. Only runs if PR is not merged and throttle allows.</summary>
    private async Task RefreshPrOnBadgeEnterAsync(int repositoryId)
    {
        if (prByRepositoryId.TryGetValue(repositoryId, out var pr) && pr?.IsMerged == true)
            return;
        if (_lastPrRefreshByRepoId.TryGetValue(repositoryId, out var last) && DateTime.UtcNow - last < PrRefreshThrottle)
            return;
        try
        {
            await WorkspacePageService.WorkspacePullRequestService.RefreshPullRequestsAsync(WorkspaceId, new[] { repositoryId });
            _lastPrRefreshByRepoId[repositoryId] = DateTime.UtcNow;
            await ReloadWorkspaceDataFromFreshScopeAsync();
            ApplySyncStateFromWorkspace();
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "PR refresh on badge enter failed for RepositoryId={RepositoryId}", repositoryId);
        }
    }

    private async Task RefreshBranchesForRepositoryAsync(int repositoryId)
    {
        try
        {
            await ScopedExecutor.ExecuteAsync<WorkspaceGitService>(
                svc => svc.RefreshBranchesAndBroadcastAsync(repositoryId, WorkspaceId, CancellationToken.None));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Branch refresh after PR merge failed for RepositoryId={RepositoryId}", repositoryId);
        }
    }

    /// <summary>Reload workspace after abort/cancel using a fresh scope. Safe to call from background job bodies. Swallows disposal exceptions so abort does not cascade errors when the circuit or context is already disposed.</summary>
    private async Task ReloadWorkspaceDataAfterCancelAsync()
    {
        if (_disposed) return;
        try
        {
            await ReloadWorkspaceDataFromFreshScopeAsync();
        }
        catch (ObjectDisposedException ex)
        {
            Logger.LogDebug(ex, "Reload after cancel skipped (context disposed) for workspace {WorkspaceId}", WorkspaceId);
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogDebug(ex, "Reload after cancel skipped (invalid operation, e.g. circuit disposed) for workspace {WorkspaceId}", WorkspaceId);
        }
        try
        {
            await InvokeAsync(() => { if (!_disposed) { ApplySyncStateFromWorkspace(); StateHasChanged(); } });
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Loads workspace using a new scope (fresh DbContext) so we get current DB values and avoid EF cache. Used by RefreshFromSync so the grid shows updated UnmatchedDeps after notify or Update.</summary>
    private async Task ReloadWorkspaceDataFromFreshScopeAsync()
    {
        var w = await ScopedExecutor.ExecuteAsync<WorkspaceRepository, Workspace?>(
            repo => repo.GetByIdAsync(WorkspaceId));
        if (w == null)
        {
            errorMessage = "Workspace not found.";
            workspaceRepositories = new List<WorkspaceRepositoryLink>();
            UpdateFilteredRepositories();
            return;
        }
        workspace = w;
        workspaceRepositories = workspace.Repositories
            .OrderByDescending(wr => wr.DependencyLevel ?? int.MinValue)
            .ThenBy(wr => GetRepositoryTypeSortOrder(wr.RepositoryType))
            .ThenByDescending(wr => wr.Dependencies ?? int.MinValue)
            .ToList();
        prByRepositoryId = BuildPrByRepositoryIdFromLinks(workspaceRepositories);
        await LoadMismatchedDependencyLinesAsync();
        UpdateFilteredRepositories();
    }

    private async Task LoadMismatchedDependencyLinesAsync()
    {
        await _loadMismatchedDepsLock.WaitAsync();
        try
        {
            // Fresh scope: circuit-scoped DbContext may be busy (e.g. CheckFileVersions during sync/update).
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var projectRepo = scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();
            var fileVersionService = scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>();

            if (!workspaceRepositories.Any(wr => (wr.UnmatchedDeps ?? 0) > 0))
            {
                _mismatchedDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<DependencyMismatchLine>>();
            }
            else
            {
                try
                {
                    // Use raw sync payloads so tag-pinned repos still get mismatch lines for hover/copy in the badge tooltip.
                    // GetUpdatePlanAsync excludes tag-pinned repos on purpose so Update does not target them.
                    var payloads = await projectRepo.GetSyncDependenciesPayloadAsync(WorkspaceId);
                    var dict = new Dictionary<int, IReadOnlyList<DependencyMismatchLine>>();
                    foreach (var p in payloads.Where(p => p.ProjectUpdates.Count > 0))
                    {
                        var lines = p.ProjectUpdates
                            .SelectMany(pu => pu.PackageUpdates)
                            .GroupBy(x => (x.PackageId.Trim(), x.CurrentVersion.Trim(), x.NewVersion.Trim()))
                            .Select(g => new DependencyMismatchLine(g.Key.Item1, g.Key.Item2, g.Key.Item3))
                            .ToList();
                        dict[p.RepoId] = lines;
                    }
                    _mismatchedDependencyLinesByRepo = dict;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not load mismatched dependency lines for workspace {WorkspaceId}", WorkspaceId);
                    _mismatchedDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<DependencyMismatchLine>>();
                }
            }

            try
            {
                var raw = await projectRepo.GetPackageDependencyLinesByRepoAsync(WorkspaceId);
                _allDependencyLinesByRepo = raw.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<DependencyLine>)kv.Value.Select(t => new DependencyLine(t.PackageId, t.Version)).ToList());
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not load dependency listing for workspace {WorkspaceId}", WorkspaceId);
                _allDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<DependencyLine>>();
            }

            try
            {
                var raw = await fileVersionService.GetMismatchedFileVersionLinesByRepoAsync(WorkspaceId);
                _mismatchedFileVersionLinesByRepo = raw.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<FileVersionMismatchLine>)kv.Value
                        .Select(t => new FileVersionMismatchLine(t.FileName, t.TokenName, t.CurrentValue, t.ExpectedValue))
                        .ToList());
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not load mismatched file version lines for workspace {WorkspaceId}", WorkspaceId);
                _mismatchedFileVersionLinesByRepo = new Dictionary<int, IReadOnlyList<FileVersionMismatchLine>>();
            }

            try
            {
                _fileLineStatusByRepo = await fileVersionService.GetFileLineStatusByWorkspaceAsync(WorkspaceId);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not load file line status for workspace {WorkspaceId}", WorkspaceId);
                _fileLineStatusByRepo = new Dictionary<int, IReadOnlyList<WorkspaceFileLineStatus>>();
            }

            try
            {
                var repoVersionMap = workspaceRepositories
                    .Where(r => r.Repository?.RepositoryName != null && !string.IsNullOrEmpty(r.GitVersion))
                    .ToDictionary(r => r.Repository!.RepositoryName!, r => r.GitVersion!, StringComparer.OrdinalIgnoreCase);
                var raw = await fileVersionService.GetAllFileVersionLinesByRepoAsync(WorkspaceId, repoVersionMap);
                _allFileVersionLinesByRepo = raw.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<FileVersionDisplayLine>)kv.Value
                        .Select(t => new FileVersionDisplayLine(t.FileName, t.TokenName, t.Version))
                        .ToList());
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not load file version lines for workspace {WorkspaceId}", WorkspaceId);
                _allFileVersionLinesByRepo = new Dictionary<int, IReadOnlyList<FileVersionDisplayLine>>();
            }

            try
            {
                var customDepRepo = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryCustomDependencyRepository>();
                _customDependencyLinesByRepo = await customDepRepo.GetCustomDependencyNamesByRepoAsync(WorkspaceId);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not load custom dependency lines for workspace {WorkspaceId}", WorkspaceId);
                _customDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<string>>();
            }
        }
        finally
        {
            _loadMismatchedDepsLock.Release();
        }
    }

    /// <summary>Called when WorkspaceSynced is received (or after an operation): reload from a fresh scope so the grid gets current DB values (no stale DbContext).</summary>
    private async Task RefreshFromSync()
    {
        await ReloadWorkspaceDataFromFreshScopeAsync();
        ApplySyncStateFromWorkspace();
        await InvokeAsync(StateHasChanged);
    }
}
