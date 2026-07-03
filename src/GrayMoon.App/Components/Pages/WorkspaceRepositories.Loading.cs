using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Queries;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private async Task LoadWorkspaceAsync()
    {
        try
        {
            isInitialLoading = true;
            errorMessage = null;
            await LoadWorkspaceHeaderAsync();
            await ResetAndLoadFromTopAsync();
            _ = RefreshPullRequestsInBackgroundAndReloadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Failed to load workspace. Please try again later.";
            _items.Clear();
            _linkByRepoId.Clear();
            _headerState = null;
        }
        finally
        {
            isInitialLoading = false;
        }
    }

    private async Task LoadWorkspaceHeaderAsync()
    {
        workspace = await WorkspacePageService.WorkspaceRepository.GetHeaderAsync(WorkspaceId);
        if (workspace == null)
        {
            errorMessage = "Workspace not found.";
        }
    }

    private async Task LoadHeaderStateAsync(CancellationToken cancellationToken = default)
    {
        _headerState = await LinkListQueryService.GetHeaderStateAsync(WorkspaceId, cancellationToken);
    }

    private async Task ResetAndLoadFromTopAsync()
    {
        if (workspace == null && errorMessage == null)
        {
            return;
        }

        if (errorMessage != null)
        {
            isInitialLoading = false;
            return;
        }

        var token = _queryLoader.BeginQueryCycle(out var generation);
        _items.Clear();
        _linkByRepoId.Clear();
        _nextCursor = null;
        hasMore = false;
        totalCount = null;
        isInitialLoading = true;
        isLoadingMore = false;
        _loadingNextChunk = false;
        prByRepositoryId = new Dictionary<int, PullRequestInfo?>();

        try
        {
            var filter = new WorkspaceRepositoryLinkListFilter(WorkspaceId, _effectiveSearch);
            await LoadHeaderStateAsync(token);
            totalCount = await LinkListQueryService.CountAsync(filter, token);
            var page = await LinkListQueryService.GetPageAsync(
                new WorkspaceRepositoryLinkListRequest(
                    WorkspaceId,
                    _effectiveSearch,
                    IWorkspaceRepositoryLinkListQueryService.DefaultChunkSize,
                    null),
                token);

            if (generation != _queryLoader.Generation || _disposed)
            {
                return;
            }

            ApplyItemsFromDtos(page.Items, replace: true);
            _nextCursor = page.NextCursor;
            hasMore = page.HasMore;
            hasLoadedOnce = true;
            await LoadMismatchedDependencyLinesAsync();
            ApplySyncStateFromLoadedItems();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _queryLoader.Generation && !_disposed)
            {
                Logger.LogError(ex, "Error loading repositories for workspace {WorkspaceId}", WorkspaceId);
                errorMessage = "Failed to load workspace. Please try again later.";
                _items.Clear();
                _linkByRepoId.Clear();
            }
        }
        finally
        {
            if (generation == _queryLoader.Generation && !_disposed)
            {
                isInitialLoading = false;
            }
        }
    }

    private async Task LoadNextChunkAsync()
    {
        if (_loadingNextChunk || isInitialLoading || !hasMore || _nextCursor is null)
        {
            return;
        }

        _loadingNextChunk = true;
        isLoadingMore = true;
        var generation = _queryLoader.Generation;
        var token = _queryLoader.GetQueryToken();

        try
        {
            var page = await LinkListQueryService.GetPageAsync(
                new WorkspaceRepositoryLinkListRequest(
                    WorkspaceId,
                    _effectiveSearch,
                    IWorkspaceRepositoryLinkListQueryService.DefaultChunkSize,
                    _nextCursor),
                token);

            if (generation != _queryLoader.Generation || _disposed)
            {
                return;
            }

            ApplyItemsFromDtos(page.Items, replace: false);
            _nextCursor = page.NextCursor;
            hasMore = page.HasMore;
            ApplySyncStateFromLoadedItems();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _queryLoader.Generation && !_disposed)
            {
                Logger.LogError(ex, "Error loading more repositories for workspace {WorkspaceId}", WorkspaceId);
                errorMessage = "Failed to load more repositories. Please try again later.";
            }
        }
        finally
        {
            _loadingNextChunk = false;
            isLoadingMore = false;
        }
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
            await ReloadWorkspaceDataFromFreshScopeAsync();

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
            ApplySyncStateFromLoadedItems();
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
            await InvokeAsync(() => { if (!_disposed) { ApplySyncStateFromLoadedItems(); StateHasChanged(); } });
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Loads workspace using a new scope (fresh DbContext) so we get current DB values and avoid EF cache. Used by RefreshFromSync so the grid shows updated UnmatchedDeps after notify or Update.</summary>
    private async Task ReloadWorkspaceDataFromFreshScopeAsync()
    {
        var w = await ScopedExecutor.ExecuteAsync<WorkspaceRepository, Workspace?>(
            repo => repo.GetHeaderAsync(WorkspaceId));
        if (w == null)
        {
            errorMessage = "Workspace not found.";
            _items.Clear();
            _linkByRepoId.Clear();
            _headerState = null;
            return;
        }

        workspace = w;
        await LoadHeaderStateAsync();
        await ResetAndLoadFromTopAsync();
    }

    private async Task LoadMismatchedDependencyLinesAsync()
    {
        await _loadMismatchedDepsLock.WaitAsync();
        try
        {
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var projectRepo = scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();
            var fileVersionService = scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>();

            if (!(_headerState?.HasUnmatchedDependencies ?? false))
            {
                _mismatchedDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<DependencyMismatchLine>>();
            }
            else
            {
                try
                {
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
                var repoVersionMap = await LinkListQueryService.GetGitVersionNameMapAsync(WorkspaceId);
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
        ApplySyncStateFromLoadedItems();
        await InvokeAsync(StateHasChanged);
    }
}
