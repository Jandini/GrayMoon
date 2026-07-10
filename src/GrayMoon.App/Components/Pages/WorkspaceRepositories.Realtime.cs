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
        if (firstRender && workspace != null && errorMessage == null && _hubConnection == null)
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(NavigationManager.ToAbsoluteUri("/hubs/workspace-sync"))
                .WithAutomaticReconnect()
                .Build();
            _hubConnection.On<int>("WorkspaceSynced", async (workspaceId) =>
            {
                if (workspaceId != WorkspaceId) return;
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
            });
            _hubConnection.On<int, int, string>("RepositoryError", (workspaceId, repositoryId, msg) =>
            {
                if (workspaceId != WorkspaceId || string.IsNullOrWhiteSpace(msg)) return;
                repositoryErrors[repositoryId] = msg;
                _ = InvokeAsync(StateHasChanged);
            });
            await _hubConnection.StartAsync();
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
            if (shouldRefreshAfterJob)
                await RefreshFromSync();
        });
    }

    private void SafeInvoke(Action callback)
    {
        if (_disposed) return;
        _ = InvokeAsync(() => { if (!_disposed) { callback(); StateHasChanged(); } });
    }
}
