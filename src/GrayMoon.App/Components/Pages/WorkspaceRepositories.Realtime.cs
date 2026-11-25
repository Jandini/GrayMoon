using GrayMoon.App.Services.Queries;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private HubConnection? _hubConnection;
    private CancellationTokenSource? _fetchRepositoriesCts;

    private async Task OnAfterRenderRealtimeAsync(bool firstRender)
    {
        if (firstRender)
        {
            try { await JSRuntime.InvokeVoidAsync("focusElement", "workspace-repos-search"); } catch { /* ignore */ }
        }
        // Not gated on firstRender: the first render can happen before the workspace is loaded.
        if (PageRealtimeGate.ShouldAttachHub(_hubConnection != null, workspace != null, _disposed))
        {
            _hubConnection = WorkspaceSyncHubConnectionHelper.Create(NavigationManager);
            _hubConnection.On<int>("WorkspaceSynced", async (workspaceId) =>
            {
                if (workspaceId != WorkspaceId) return;
                await RefreshFromSyncDebouncedAsync();
            });
            // A hook sync of a Feature raises only the Context* events (WorkspaceSynced is sent for the special
            // Workspace), so without this a Feature grid showed a commit or checkout made in a terminal only at its
            // next reload. The special Workspace keeps WorkspaceSynced: it is always sent with its ContextSynced.
            _hubConnection.On<int, int>("ContextSynced", async (workspaceId, contextId) =>
            {
                if (workspaceId != WorkspaceId || !_isFeatureContext || _selectedContextId?.Value != contextId) return;
                await RefreshFromSyncDebouncedAsync();
            });
            _hubConnection.On<int, int, string>("RepositoryError", (workspaceId, repositoryId, msg) =>
            {
                if (workspaceId != WorkspaceId || string.IsNullOrWhiteSpace(msg)) return;
                SetRepositoryError(repositoryId, msg);
                _ = InvokeAsync(StateHasChanged);
            });
            _hubConnection.On<int, int>("GitChangesUpdated", async (workspaceId, repositoryId) =>
            {
                if (workspaceId != WorkspaceId) return;
                if (IsBackgroundJobRunning)
                {
                    _pendingRefreshAfterJob = true;
                    return;
                }

                if (!_linkByRepoId.ContainsKey(repositoryId))
                    return;

                try
                {
                    var dto = await LinkListQueryService.GetSnapshotAsync(
                        WorkspaceId, repositoryId, _selectedContextId, !_isFeatureContext);
                    if (dto is null || _disposed)
                        return;

                    await InvokeAsync(() =>
                    {
                        if (_disposed)
                            return;
                        CacheLink(WorkspaceRepositoryLinkListMapper.ToLink(dto), WorkspaceRepositoryLinkListMapper.ToPullRequestInfo(dto));
                        StateHasChanged();
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "GitChangesUpdated snapshot refresh failed for workspace {WorkspaceId} repo {RepositoryId}", workspaceId, repositoryId);
                }
            });
            _ = WorkspaceSyncHubConnectionHelper.TryStartAsync(_hubConnection, Logger);
        }
    }

    /// <summary>Reloads the grid after a sync event, once for a run of events; deferred to the end of a running job.</summary>
    private async Task RefreshFromSyncDebouncedAsync()
    {
        if (IsBackgroundJobRunning)
        {
            _pendingRefreshAfterJob = true;
            return;
        }
        CancellationTokenSource cts;
        lock (_refreshDebounceLock)
        {
            _refreshDebounceCts?.Cancel();
            _refreshDebounceCts?.Dispose();
            _refreshDebounceCts = new CancellationTokenSource();
            cts = _refreshDebounceCts;
        }
        try
        {
            await Task.Delay(RefreshDebounceMs, cts.Token);
            await InvokeAsync(RefreshFromSync);
        }
        catch (OperationCanceledException)
        {
            /* debounced */
        }
        finally
        {
            lock (_refreshDebounceLock)
            {
                if (cts == _refreshDebounceCts)
                {
                    _refreshDebounceCts?.Dispose();
                    _refreshDebounceCts = null;
                }
            }
        }
    }

    /// <summary>Drops a grid refresh that is waiting for the debounce or for the end of a job. Dispatcher only.</summary>
    private void CancelPendingGridRefresh()
    {
        _pendingRefreshAfterJob = false;
        lock (_refreshDebounceLock)
        {
            _refreshDebounceCts?.Cancel();
        }
    }

    private void OnQueueStateChanged(object? sender, EventArgs e) => _ = InvokeAsync(StateHasChanged);

    private void OnJobServiceChanged()
    {
        if (_disposed) return;
        var shouldRefreshAfterJob = _pendingRefreshAfterJob && !IsBackgroundJobRunning;
        if (shouldRefreshAfterJob)
            _pendingRefreshAfterJob = false;
        // Overlay / IsJobRunning UI only. Grid refresh runs inside job bodies
        // (RefreshOnSuccess / ReloadWorkspaceDataAfterCancelAsync), not here.
        _ = InvokeAsync(async () =>
        {
            if (_disposed) return;
            StateHasChanged();
            // Debounced, so a context switch that follows the job at once (Create Feature selects the new
            // Feature) cancels it in CancelPendingGridRefresh instead of loading the context being left.
            if (shouldRefreshAfterJob)
                await RefreshFromSyncDebouncedAsync();
        });
    }

    private void SafeInvoke(Action callback)
    {
        if (_disposed) return;
        _ = InvokeAsync(() => { if (!_disposed) { callback(); StateHasChanged(); } });
    }
}
