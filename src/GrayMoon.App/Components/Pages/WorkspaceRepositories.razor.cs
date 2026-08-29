using GrayMoon.App.Services;
using GrayMoon.App.Services.Queries;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
namespace GrayMoon.App.Components.Pages;
public sealed partial class WorkspaceRepositories : IAsyncDisposable, IDisposable
{
    [Parameter] public int WorkspaceId { get; set; }
    [Inject] private IWorkspacePageService WorkspacePageService { get; set; } = default!;
    [Inject] private IServiceScopeFactory ServiceScopeFactory { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<WorkspaceRepositories> Logger { get; set; } = default!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] private IToastService ToastService { get; set; } = default!;
    [Inject] private IOptions<GrayMoon.App.Models.WorkspaceOptions> WorkspaceOptions { get; set; } = default!;
    [Inject] private WorkspaceCommitSyncHandler WorkspaceCommitSyncHandler { get; set; } = default!;
    [Inject] private WorkspaceSyncHandler WorkspaceSyncHandler { get; set; } = default!;
    [Inject] private WorkspaceUpdateHandler WorkspaceUpdateHandler { get; set; } = default!;
    [Inject] private WorkspaceFileVersionService FileVersionService { get; set; } = default!;
    [Inject] private WorkspacePushHandler WorkspacePushHandler { get; set; } = default!;
    [Inject] private WorkspaceUndoPushHandler UndoPushHandler { get; set; } = default!;
    [Inject] private WorkspaceDependencyService WorkspaceDependencyService { get; set; } = default!;
    [Inject] private WorkspaceBranchHandler WorkspaceBranchHandler { get; set; } = default!;
    [Inject] private NewFeatureOrchestrator NewFeatureOrchestrator { get; set; } = default!;
    [Inject] private AgentQueueStateService AgentQueueStateService { get; set; } = default!;
    [Inject] private IPullRequestService PullRequestService { get; set; } = default!;
    [Inject] private IBackgroundJobService JobService { get; set; } = default!;
    [Inject] private IScopedServiceExecutor ScopedExecutor { get; set; } = default!;
    [Inject] private IWorkspaceRepositoryLinkListQueryService LinkListQueryService { get; set; } = default!;
    [Inject] private WorkspacePendingActionsService PendingActionsService { get; set; } = default!;
    [Inject] private AppActivityStateService ActivityStateService { get; set; } = default!;

    private const string SyncModeStorageKey = "graymoon:sync-mode";
    private bool _quickFetchIsPrimary;

    protected override async Task OnInitializedAsync()
    {
        AgentQueueStateService.OnQueueStateChanged(OnQueueStateChanged);
        JobService.Changed += OnJobServiceChanged;
        _loadedWorkspaceId = WorkspaceId;
        var storedMode = await JSRuntime.InvokeAsync<string?>("graymoonStorageGet", SyncModeStorageKey);
        _quickFetchIsPrimary = storedMode == "quick-fetch";
        await LoadPendingRestoreScrollTopAsync();
        await LoadWorkspaceAsync();
        ApplySyncStateFromLoadedItems();
        StartPrPollingLoop();
    }

    /// <summary>Reads the saved tbody scroll offset for the current WorkspaceId from sessionStorage, consumed by the next ResetAndLoadFromTopAsync(restoreScroll: true) call. Best-effort: malformed or missing storage falls back to no restore (top of grid), matching current behavior.</summary>
    private async Task LoadPendingRestoreScrollTopAsync()
    {
        try
        {
            var raw = await JSRuntime.InvokeAsync<string?>("graymoonSessionStorageGet", ScrollStorageKey);
            _pendingRestoreScrollTop = double.TryParse(raw, out var value) && value > 0 ? value : null;
        }
        catch (JSDisconnectedException)
        {
            _pendingRestoreScrollTop = null;
        }
        catch (InvalidOperationException)
        {
            _pendingRestoreScrollTop = null;
        }
    }

    private async Task SetSyncModeAsync(bool quickFetch)
    {
        _quickFetchIsPrimary = quickFetch;
        await JSRuntime.InvokeVoidAsync("graymoonStorageSet", SyncModeStorageKey, quickFetch ? "quick-fetch" : "sync");
    }
    protected override async Task OnParametersSetAsync()
    {
        if (_loadedWorkspaceId == WorkspaceId || _disposed)
        {
            return;
        }
        CancelBackgroundWork();
        await DetachVirtualScrollAsync();
        _loadedWorkspaceId = WorkspaceId;
        errorMessage = null;
        hasLoadedOnce = false;
        ClearGridState();
        await LoadPendingRestoreScrollTopAsync();
        await LoadWorkspaceAsync();
        ApplySyncStateFromLoadedItems();
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await OnAfterRenderRealtimeAsync(firstRender);
        if (!isInitialLoading && _slots.Count > 0 && !_virtualScrollAttached && !_disposed)
        {
            await AttachVirtualScrollAsync();
        }
    }
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPrPollingLoop();
        CancelBackgroundWork();
        AgentQueueStateService.RemoveQueueStateChanged(OnQueueStateChanged);
        JobService.Changed -= OnJobServiceChanged;
        lock (_refreshDebounceLock)
        {
            _refreshDebounceCts?.Cancel();
            _refreshDebounceCts?.Dispose();
            _refreshDebounceCts = null;
        }
        _ = _hubConnection?.StopAsync();
        _ = _hubConnection?.DisposeAsync().AsTask();
        _fetchRepositoriesCts?.Cancel();
        _fetchRepositoriesCts?.Dispose();
        _queryLoader.Dispose();
        _reloadGate.Dispose();
        _virtualScrollDotNetRef?.Dispose();
        _virtualScrollDotNetRef = null;
    }
    public async ValueTask DisposeAsync()
    {
        await DetachVirtualScrollAsync();
        Dispose();
    }
}
