using GrayMoon.App.Services;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application.Features;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
namespace GrayMoon.App.Components.Pages;
public sealed partial class WorkspaceRepositories : IAsyncDisposable, IDisposable
{
    [Parameter] public int WorkspaceId { get; set; }
    [SupplyParameterFromQuery(Name = "context")] public int? ContextQuery { get; set; }
    [Inject] private IWorkspacePageService WorkspacePageService { get; set; } = default!;
    [Inject] private IServiceScopeFactory ServiceScopeFactory { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<WorkspaceRepositories> Logger { get; set; } = default!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] private IToastService ToastService { get; set; } = default!;
    [Inject] private IOptions<GrayMoon.App.Models.WorkspaceOptions> WorkspaceOptions { get; set; } = default!;
    [Inject] private WorkspaceFileVersionService FileVersionService { get; set; } = default!;
    [Inject] private IWorkspacePushOperations PushOperations { get; set; } = default!;
    [Inject] private IWorkspacePullRequestOperations PullRequestOperations { get; set; } = default!;
    [Inject] private WorkspaceDependencyService WorkspaceDependencyService { get; set; } = default!;
    [Inject] private WorkspaceBranchHandler WorkspaceBranchHandler { get; set; } = default!;
    [Inject] private AgentQueueStateService AgentQueueStateService { get; set; } = default!;
    [Inject] private IBackgroundJobService JobService { get; set; } = default!;
    [Inject] private IScopedServiceExecutor ScopedExecutor { get; set; } = default!;
    [Inject] private IWorkspaceRepositoryLinkListQueryService LinkListQueryService { get; set; } = default!;
    [Inject] private WorkspacePendingActionsService PendingActionsService { get; set; } = default!;
    [Inject] private AppActivityStateService ActivityStateService { get; set; } = default!;
    [Inject] private IWorkspaceFeatureContextResolver FeatureContextResolver { get; set; } = default!;
    [Inject] private IWorkspaceSelectedFeatureContextService SelectedFeatureContextService { get; set; } = default!;

    private WorkspaceFeatureContextId? _selectedContextId;
    private bool _isFeatureContext;
    private bool _createFeatureModalVisible;
    private string? _createFeatureInitialName;
    private bool _removeFeatureModalVisible;
    private WorkspaceFeatureContextId? _removeFeatureContextId;

    private const string SyncModeStorageKey = "graymoon:sync-mode";
    private bool _quickFetchIsPrimary;

    protected override async Task OnInitializedAsync()
    {
        AgentQueueStateService.OnQueueStateChanged(OnQueueStateChanged);
        JobService.Changed += OnJobServiceChanged;
        _loadedWorkspaceId = WorkspaceId;
        EnsureGitChangesActivation();
        var storedMode = await JSRuntime.InvokeAsync<string?>("graymoonStorageGet", SyncModeStorageKey);
        _quickFetchIsPrimary = storedMode == "quick-fetch";
        await ResolveSelectedContextAsync();
        await LoadPendingRestoreScrollTopAsync();
        await LoadWorkspaceAsync();
        ApplySyncStateFromLoadedItems();
        StartPrPollingLoop();
    }

    private async Task ResolveSelectedContextAsync()
    {
        try
        {
            if (ContextQuery is int q && q > 0)
            {
                var info = await FeatureContextResolver.GetRequiredAsync(new WorkspaceFeatureContextId(q), WorkspaceId);
                _selectedContextId = info.ContextId;
                _isFeatureContext = !info.IsSpecialWorkspace;
                await SelectedFeatureContextService.SetSelectedAsync(WorkspaceId, info.ContextId);
                return;
            }

            var preferred = await SelectedFeatureContextService.GetSelectedAsync(WorkspaceId);
            if (preferred is WorkspaceFeatureContextId preferredId)
            {
                var info = await FeatureContextResolver.GetRequiredAsync(preferredId, WorkspaceId);
                _selectedContextId = info.ContextId;
                _isFeatureContext = !info.IsSpecialWorkspace;
                if (_isFeatureContext)
                {
                    var path = new Uri(NavigationManager.Uri).GetLeftPart(UriPartial.Path);
                    NavigationManager.NavigateTo($"{path}?context={preferredId.Value}", replace: true);
                }
                return;
            }

            var special = await FeatureContextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(WorkspaceId);
            _selectedContextId = special;
            _isFeatureContext = false;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to resolve Feature context for workspace {WorkspaceId}", WorkspaceId);
            var special = await FeatureContextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(WorkspaceId);
            _selectedContextId = special;
            _isFeatureContext = false;
        }
    }

    private WorkspaceFeatureContextId RequireSelectedContextId()
        => _selectedContextId
           ?? throw new InvalidOperationException("Workspace Feature context is not resolved for this page.");

    private async Task OnSelectedContextChangedAsync(WorkspaceFeatureContextId contextId)
    {
        WorkspaceFeatureContextInfo info;
        try
        {
            info = await FeatureContextResolver.GetRequiredAsync(contextId, WorkspaceId);
        }
        catch (Exception ex)
        {
            // The selector's option list can be briefly stale (e.g. right after another tab/user removed
            // this Feature). Fall back to the special Workspace context instead of letting the resolver's
            // exception bubble up and tear down the circuit.
            Logger.LogWarning(ex, "Selected context {ContextId} could not be resolved for workspace {WorkspaceId}; falling back to Workspace.", contextId.Value, WorkspaceId);
            ToastService.Show("That Feature no longer exists. Switched back to Workspace.");
            info = await FeatureContextResolver.GetRequiredAsync(
                await FeatureContextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(WorkspaceId),
                WorkspaceId);
            var fallbackPath = new Uri(NavigationManager.Uri).GetLeftPart(UriPartial.Path);
            NavigationManager.NavigateTo(fallbackPath, replace: true);
        }

        _selectedContextId = info.ContextId;
        _isFeatureContext = !info.IsSpecialWorkspace;
        await SelectedFeatureContextService.SetSelectedAsync(WorkspaceId, info.ContextId);
        await InvokeAsync(async () =>
        {
            if (_disposed) return;
            ClearGridState();
            await LoadWorkspaceAsync();
            ApplySyncStateFromLoadedItems();
            StateHasChanged();
        });
    }

    private Task OnRequestCreateFeatureAsync(string name)
    {
        _createFeatureInitialName = name;
        _createFeatureModalVisible = true;
        return Task.CompletedTask;
    }

    private Task OnRemoveFeatureAsync()
    {
        if (_isFeatureContext && _selectedContextId is WorkspaceFeatureContextId ctx)
        {
            _removeFeatureContextId = ctx;
            _removeFeatureModalVisible = true;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The "-" icon on a Feature row inside the context-switcher dropdown (WorkspaceFeatureSelector): opens
    /// Remove Feature for that specific Feature, regardless of which context is currently selected/viewed.
    /// </summary>
    private Task OnRequestRemoveFeatureFromSelectorAsync(WorkspaceFeatureContextId contextId)
    {
        _removeFeatureContextId = contextId;
        _removeFeatureModalVisible = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// A branch in the Switch Branch dialog was owned by a Feature worktree (§28A): rather than attempting an
    /// ordinary git branch delete (which the worktree would reject anyway), route straight to Remove Feature
    /// for that Feature's own context.
    /// </summary>
    private Task OnRequestFeatureCleanupFromBranchModalAsync(WorkspaceFeatureContextId featureContextId)
    {
        _removeFeatureContextId = featureContextId;
        _removeFeatureModalVisible = true;
        return Task.CompletedTask;
    }

    private async Task OnFeatureCreatedAsync(CreateFeatureResult result)
    {
        _createFeatureModalVisible = false;
        if (result.ContextId is WorkspaceFeatureContextId created)
        {
            await OnSelectedContextChangedAsync(created);
            var path = new Uri(NavigationManager.Uri).GetLeftPart(UriPartial.Path);
            NavigationManager.NavigateTo($"{path}?context={created.Value}", replace: true);
            ToastService.Show($"Feature created.");
        }
    }

    private async Task OnFeatureRemovedAsync()
    {
        _removeFeatureModalVisible = false;

        // Only force a navigation away from the current view when the Feature that was just removed is the
        // one being viewed (e.g. removed via the "Feature" button's own "Remove Feature" menu item, or via the
        // "-" icon on the currently-selected row in the context-switcher dropdown). Removing a *different*
        // Feature from that dropdown's "-" icon shouldn't kick the user out of whatever context they're
        // currently viewing.
        var removedCurrentContext = _removeFeatureContextId is { } removedId
            && _selectedContextId is { } selectedId
            && removedId.Value == selectedId.Value;
        _removeFeatureContextId = null;

        if (removedCurrentContext)
        {
            var special = await FeatureContextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(WorkspaceId);
            await SelectedFeatureContextService.SetSelectedAsync(WorkspaceId, special);
            await OnSelectedContextChangedAsync(special);
            var path = new Uri(NavigationManager.Uri).GetLeftPart(UriPartial.Path);
            NavigationManager.NavigateTo(path, replace: true);
        }
        ToastService.Show("Feature removed.");
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
        EnsureGitChangesActivation();
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
        ReleaseGitChangesActivation();
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
        WorkspaceSyncHubConnectionHelper.DisposeFireAndForget(_hubConnection);
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
