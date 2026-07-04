using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Queries;
using Microsoft.JSInterop;
namespace GrayMoon.App.Components.Pages;
public sealed partial class WorkspaceRepositories
{
    private async Task LoadWorkspaceAsync()
    {
        try
        {
            isInitialLoading = true;
            errorMessage = null;
            CancelBackgroundWork();
            _backgroundWorkCts = new CancellationTokenSource();
            await LoadWorkspaceHeaderAsync();
            await ResetAndLoadFromTopAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Failed to load workspace. Please try again later.";
            ClearGridState();
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
        ClearGridState();
        isInitialLoading = true;
        _virtualScrollAttached = false;
        try
        {
            var filter = new WorkspaceRepositoryLinkListFilter(WorkspaceId, _effectiveSearch);
            await LoadHeaderStateAsync(token);
            totalCount = await LinkListQueryService.CountAsync(filter, token);
            var index = await LinkListQueryService.GetIndexAsync(filter, token);
            if (generation != _queryLoader.Generation || _disposed)
            {
                return;
            }
            BuildSlots(index);
            hasLoadedOnce = true;
            _loadedWorkspaceId = WorkspaceId;
            var initialEnd = Math.Min(_slots.Count - 1, VirtualInitialViewportSlots - 1);
            UpdateVisibleRange(0, Math.Max(-1, initialEnd));
            await EnsureSlotsHydratedAsync(0, initialEnd, token);
            if (generation != _queryLoader.Generation || _disposed)
            {
                return;
            }
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
                ClearGridState();
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
    private async Task<bool> EnsureSlotsHydratedAsync(int start, int end, CancellationToken cancellationToken)
    {
        if (_slots.Count == 0 || end < start)
        {
            return false;
        }
        start = Math.Clamp(start, 0, _slots.Count - 1);
        end = Math.Clamp(end, start, _slots.Count - 1);
        var missingIds = new List<int>();
        for (var i = start; i <= end; i++)
        {
            var slot = _slots[i];
            if (slot.Kind != VirtualSlotKind.Row)
            {
                continue;
            }
            if (!_linkByWrlId.ContainsKey(slot.WorkspaceRepositoryId))
            {
                missingIds.Add(slot.WorkspaceRepositoryId);
            }
        }
        if (missingIds.Count == 0)
        {
            return false;
        }
        var dtos = await LinkListQueryService.GetByIdsAsync(WorkspaceId, missingIds, cancellationToken);
        if (cancellationToken.IsCancellationRequested || _disposed)
        {
            return false;
        }
        ApplyItemsFromDtos(dtos, replace: false);
        return dtos.Count > 0;
    }
    [JSInvokable]
    public async Task OnVirtualScroll(double scrollTop, double clientHeight)
    {
        if (_disposed || _slots.Count == 0)
        {
            return;
        }

        var scrollGeneration = Interlocked.Increment(ref _scrollGeneration);
        var queryGeneration = _queryLoader.Generation;
        var token = _queryLoader.GetQueryToken();
        var rangeStart = scrollTop - (VirtualOverscanSlots * VirtualRowHeightPx);
        var rangeEnd = scrollTop + clientHeight + (VirtualOverscanSlots * VirtualRowHeightPx);
        if (rangeStart < 0)
        {
            rangeStart = 0;
        }
        var start = 0;
        var end = _slots.Count - 1;
        double cumulative = 0;
        var foundStart = false;
        for (var i = 0; i < _slots.Count; i++)
        {
            var height = SlotHeight(_slots[i]);
            var slotBottom = cumulative + height;
            if (!foundStart && slotBottom > rangeStart)
            {
                start = i;
                foundStart = true;
            }
            if (cumulative < rangeEnd)
            {
                end = i;
            }
            else if (foundStart)
            {
                break;
            }
            cumulative = slotBottom;
        }
        if (!foundStart)
        {
            start = 0;
            end = Math.Min(_slots.Count - 1, VirtualInitialViewportSlots - 1);
        }

        var rangeChanged = UpdateVisibleRange(start, end);
        var itemsHydrated = await EnsureSlotsHydratedAsync(start, end, token);
        if (scrollGeneration != _scrollGeneration
            || queryGeneration != _queryLoader.Generation
            || _disposed)
        {
            return;
        }

        if (rangeChanged || itemsHydrated)
        {
            await InvokeAsync(StateHasChanged);
        }
    }
    private async Task AttachVirtualScrollAsync()
    {
        if (_disposed || _virtualScrollAttached || _slots.Count == 0)
        {
            return;
        }
        try
        {
            _virtualScrollDotNetRef ??= DotNetObjectReference.Create(this);
            await JSRuntime.InvokeVoidAsync(
                "grayMoonVirtualScroll.attach",
                _tbodyRef,
                _virtualScrollDotNetRef,
                TotalScrollHeightPx());
            _virtualScrollAttached = true;
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
    private async Task DetachVirtualScrollAsync()
    {
        if (!_virtualScrollAttached)
        {
            return;
        }
        try
        {
            await JSRuntime.InvokeVoidAsync("grayMoonVirtualScroll.detach", _tbodyRef);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        _virtualScrollAttached = false;
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
            await PatchRepositoryRowAsync(repositoryId);
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "PR refresh on badge enter failed for RepositoryId={RepositoryId}", repositoryId);
        }
    }
    private async Task PatchRepositoryRowAsync(int repositoryId)
    {
        var dto = await LinkListQueryService.GetSnapshotAsync(WorkspaceId, repositoryId);
        if (dto is null)
        {
            return;
        }
        CacheLink(
            WorkspaceRepositoryLinkListMapper.ToLink(dto),
            WorkspaceRepositoryLinkListMapper.ToPullRequestInfo(dto));
    }
    private async Task RefreshVisibleRowsAsync(CancellationToken cancellationToken = default)
    {
        await LoadHeaderStateAsync(cancellationToken);
        totalCount = await LinkListQueryService.CountAsync(
            new WorkspaceRepositoryLinkListFilter(WorkspaceId, _effectiveSearch),
            cancellationToken);
        if (_visibleEnd >= _visibleStart && _slots.Count > 0)
        {
            var ids = new List<int>();
            for (var i = _visibleStart; i <= _visibleEnd && i < _slots.Count; i++)
            {
                if (_slots[i].Kind == VirtualSlotKind.Row)
                {
                    ids.Add(_slots[i].WorkspaceRepositoryId);
                }
            }
            if (ids.Count > 0)
            {
                var dtos = await LinkListQueryService.GetByIdsAsync(WorkspaceId, ids, cancellationToken);
                ApplyItemsFromDtos(dtos, replace: false);
            }
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
            ClearGridState();
            return;
        }
        workspace = w;
        await ResetAndLoadFromTopAsync();
    }
    /// <summary>Called when WorkspaceSynced is received (or after an operation): refresh header + visible rows only.</summary>
    private async Task RefreshFromSync()
    {
        if (_disposed) return;
        try
        {
            var token = _queryLoader.GetQueryToken();
            var w = await ScopedExecutor.ExecuteAsync<WorkspaceRepository, Workspace?>(
                repo => repo.GetHeaderAsync(WorkspaceId));
            if (w == null)
            {
                errorMessage = "Workspace not found.";
                ClearGridState();
                await InvokeAsync(StateHasChanged);
                return;
            }
            workspace = w;
            // Rebuild index when levels/order may have changed; keep scroll position via virtual scroll.
            var filter = new WorkspaceRepositoryLinkListFilter(WorkspaceId, _effectiveSearch);
            var index = await LinkListQueryService.GetIndexAsync(filter, token);
            if (_disposed) return;
            BuildSlots(index);
            totalCount = index.Count;
            await LoadHeaderStateAsync(token);
            _linkByRepoId.Clear();
            _linkByWrlId.Clear();
            prByRepositoryId = new Dictionary<int, PullRequestInfo?>();
            repoSyncStatus = new();
            _tooltipLoadedRepoIds.Clear();
            _tooltipLoadInFlight.Clear();
            if (_visibleEnd < 0 || _visibleStart >= _slots.Count)
            {
                var initialEnd = Math.Min(_slots.Count - 1, VirtualInitialViewportSlots - 1);
                UpdateVisibleRange(0, Math.Max(-1, initialEnd));
            }
            else
            {
                UpdateVisibleRange(_visibleStart, Math.Min(_visibleEnd, _slots.Count - 1));
            }
            await EnsureSlotsHydratedAsync(_visibleStart, _visibleEnd, token);
            if (_virtualScrollAttached)
            {
                try
                {
                    await JSRuntime.InvokeVoidAsync("grayMoonVirtualScroll.setTotalHeight", _tbodyRef, TotalScrollHeightPx());
                }
                catch (JSDisconnectedException) { }
                catch (InvalidOperationException) { }
            }
            ApplySyncStateFromLoadedItems();
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "RefreshFromSync failed for workspace {WorkspaceId}", WorkspaceId);
        }
    }
    private void CancelBackgroundWork()
    {
        try
        {
            _backgroundWorkCts?.Cancel();
            _backgroundWorkCts?.Dispose();
        }
        catch
        {
        }
        _backgroundWorkCts = null;
        _queryLoader.CancelQuery();
    }
    private async Task EnsureTooltipDataForRepoAsync(int repositoryId)
    {
        if (_tooltipLoadedRepoIds.Contains(repositoryId) || !_tooltipLoadInFlight.Add(repositoryId))
        {
            return;
        }
        try
        {
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var projectRepo = scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();
            var fileVersionService = scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>();
            var customDepRepo = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryCustomDependencyRepository>();
            var mismatched = await projectRepo.GetMismatchedDependencyLinesForRepoAsync(WorkspaceId, repositoryId);
            var allDeps = await projectRepo.GetPackageDependencyLinesForRepoAsync(WorkspaceId, repositoryId);
            var mismatchedFiles = await fileVersionService.GetMismatchedFileVersionLinesForRepoAsync(WorkspaceId, repositoryId);
            var fileStatuses = await fileVersionService.GetFileLineStatusForRepoAsync(WorkspaceId, repositoryId);
            var repoVersionMap = await LinkListQueryService.GetGitVersionNameMapAsync(WorkspaceId);
            var allFileLines = await fileVersionService.GetAllFileVersionLinesForRepoAsync(WorkspaceId, repositoryId, repoVersionMap);
            var custom = await customDepRepo.GetCustomDependencyNamesForRepoAsync(WorkspaceId, repositoryId);
            var mismatchDict = _mismatchedDependencyLinesByRepo as Dictionary<int, IReadOnlyList<DependencyMismatchLine>>
                ?? _mismatchedDependencyLinesByRepo.ToDictionary(kv => kv.Key, kv => kv.Value);
            mismatchDict[repositoryId] = mismatched
                .Select(t => new DependencyMismatchLine(t.PackageId, t.CurrentVersion, t.NewVersion))
                .ToList();
            _mismatchedDependencyLinesByRepo = mismatchDict;
            var allDepDict = _allDependencyLinesByRepo as Dictionary<int, IReadOnlyList<DependencyLine>>
                ?? _allDependencyLinesByRepo.ToDictionary(kv => kv.Key, kv => kv.Value);
            allDepDict[repositoryId] = allDeps.Select(t => new DependencyLine(t.PackageId, t.Version)).ToList();
            _allDependencyLinesByRepo = allDepDict;
            var mismatchFileDict = _mismatchedFileVersionLinesByRepo as Dictionary<int, IReadOnlyList<FileVersionMismatchLine>>
                ?? _mismatchedFileVersionLinesByRepo.ToDictionary(kv => kv.Key, kv => kv.Value);
            mismatchFileDict[repositoryId] = mismatchedFiles
                .Select(t => new FileVersionMismatchLine(t.FileName, t.TokenName, t.CurrentValue, t.ExpectedValue))
                .ToList();
            _mismatchedFileVersionLinesByRepo = mismatchFileDict;
            var fileStatusDict = _fileLineStatusByRepo as Dictionary<int, IReadOnlyList<WorkspaceFileLineStatus>>
                ?? _fileLineStatusByRepo.ToDictionary(kv => kv.Key, kv => kv.Value);
            fileStatusDict[repositoryId] = fileStatuses;
            _fileLineStatusByRepo = fileStatusDict;
            var allFileDict = _allFileVersionLinesByRepo as Dictionary<int, IReadOnlyList<FileVersionDisplayLine>>
                ?? _allFileVersionLinesByRepo.ToDictionary(kv => kv.Key, kv => kv.Value);
            allFileDict[repositoryId] = allFileLines
                .Select(t => new FileVersionDisplayLine(t.FileName, t.TokenName, t.Version))
                .ToList();
            _allFileVersionLinesByRepo = allFileDict;
            var customDict = _customDependencyLinesByRepo as Dictionary<int, IReadOnlyList<string>>
                ?? _customDependencyLinesByRepo.ToDictionary(kv => kv.Key, kv => kv.Value);
            customDict[repositoryId] = custom;
            _customDependencyLinesByRepo = customDict;
            _tooltipLoadedRepoIds.Add(repositoryId);
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Tooltip load failed for RepositoryId={RepositoryId}", repositoryId);
        }
        finally
        {
            _tooltipLoadInFlight.Remove(repositoryId);
        }
    }
    private void OnDependencyBadgeMouseEnter(int repositoryId) =>
        _ = EnsureTooltipDataForRepoAsync(repositoryId);
}
