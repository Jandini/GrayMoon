using GrayMoon.App.Services;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application;
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
    [Inject] private IWorkspaceFeatureOperations FeatureOperations { get; set; } = default!;
    [Inject] private WorkspaceContextNavigationService ContextNavigation { get; set; } = default!;

    /// <summary>Keeps modal deep-links on the Feature currently being viewed (Workspace URLs stay bare).</summary>
    private string BuildContextScopedUrl(string relativePathWithoutQuery)
        => ContextNavigation.AppendContextQuery(relativePathWithoutQuery, _selectedContextId, !_isFeatureContext);

    private WorkspaceFeatureContextId? _selectedContextId;
    private bool _isFeatureContext;
    /// <summary>Last <see cref="ContextQuery"/> value applied to grid state - detects URL context switches.</summary>
    private int? _boundContextQuery;
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
        var storedMode = await JSRuntime.InvokeAsync<string?>("graymoonStorageGet", SyncModeStorageKey);
        _quickFetchIsPrimary = storedMode == "quick-fetch";
        await ResolveSelectedContextAsync();
        _boundContextQuery = BoundContextQueryFromSelection();
        await LoadPendingRestoreScrollTopAsync();
        await LoadWorkspaceAsync();
        ApplySyncStateFromLoadedItems();
        StartPrPollingLoop();
    }

    private async Task ResolveSelectedContextAsync()
    {
        try
        {
            var info = await ContextNavigation.ResolveForPageAsync(WorkspaceId, ContextQuery);
            _selectedContextId = info.ContextId;
            _isFeatureContext = !info.IsSpecialWorkspace;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to resolve Feature context for workspace {WorkspaceId}", WorkspaceId);
            var special = await FeatureContextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(WorkspaceId);
            _selectedContextId = special;
            _isFeatureContext = false;
        }
    }

    /// <summary>
    /// Query value that matches the resolved context (Feature id, or null for Workspace) so a
    /// follow-up <see cref="NavigateTo"/> canonicalize does not look like a user context switch.
    /// </summary>
    private int? BoundContextQueryFromSelection()
        => _isFeatureContext ? _selectedContextId?.Value : null;

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
        Interlocked.Increment(ref _contextGeneration);
        // Drop Feature/Workspace rows immediately so the selector label and grid cannot disagree
        // while LoadWorkspaceAsync is still queued (InvokeAsync runs after the current turn).
        ClearGridState();
        isInitialLoading = true;
        await SelectedFeatureContextService.SetSelectedAsync(WorkspaceId, info.ContextId);
        await InvokeAsync(async () =>
        {
            if (_disposed) return;
            await LoadWorkspaceAsync();
            ApplySyncStateFromLoadedItems();
            StateHasChanged();
        });
    }

    /// <summary>
    /// Applies a URL <c>?context=</c> change that was not already handled by
    /// <see cref="OnSelectedContextChangedAsync"/> (browser back/forward, or the selector's
    /// <c>NavigateTo</c> after the EventCallback). Empty query means Workspace - do not revive a
    /// stored Feature preference or the grid stays on Feature branches after switching away.
    /// </summary>
    private async Task SyncContextFromQueryAsync()
    {
        WorkspaceFeatureContextId desired;
        try
        {
            if (ContextQuery is int q && q > 0)
            {
                var info = await FeatureContextResolver.GetRequiredAsync(new WorkspaceFeatureContextId(q), WorkspaceId);
                desired = info.ContextId;
            }
            else
            {
                desired = await FeatureContextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(WorkspaceId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to sync context from query for workspace {WorkspaceId}", WorkspaceId);
            desired = await FeatureContextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(WorkspaceId);
        }

        if (_selectedContextId is { } current && current.Value == desired.Value)
            return;

        await OnSelectedContextChangedAsync(desired);
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
        if (_disposed)
            return;

        if (_loadedWorkspaceId != WorkspaceId)
        {
            CancelBackgroundWork();
            await DetachVirtualScrollAsync();
            _loadedWorkspaceId = WorkspaceId;
            errorMessage = null;
            hasLoadedOnce = false;
            ClearGridState();
            await ResolveSelectedContextAsync();
            _boundContextQuery = BoundContextQueryFromSelection();
            await LoadPendingRestoreScrollTopAsync();
            await LoadWorkspaceAsync();
            ApplySyncStateFromLoadedItems();
            return;
        }

        // Same workspace: selector NavigateTo / browser history changed ?context=.
        if (_boundContextQuery != ContextQuery)
        {
            _boundContextQuery = ContextQuery;
            await SyncContextFromQueryAsync();
        }
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
