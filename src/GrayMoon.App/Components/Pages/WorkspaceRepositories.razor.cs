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
    private string? _createFeatureWorkspaceBranch;
    private bool _removeFeatureModalVisible;
    private WorkspaceFeatureContextId? _removeFeatureContextId;
    private RemoveFeaturePlan? _removeFeaturePlan;

    private const string SyncModeStorageKey = "graymoon:sync-mode";
    private bool _quickFetchIsPrimary;
    private bool _prPollingStarted;

    protected override void OnInitialized()
    {
        AgentQueueStateService.OnQueueStateChanged(OnQueueStateChanged);
        JobService.Changed += OnJobServiceChanged;
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
        _boundContextQuery = BoundContextQueryFromSelection();
        Interlocked.Increment(ref _contextGeneration);
        // Drop Feature/Workspace rows immediately so the selector label and grid cannot disagree
        // while LoadWorkspaceAsync is still queued (InvokeAsync runs after the current turn).
        // Keep header action flags so Branch/Create PR does not flash during the reload.
        ClearGridState(clearHeaderState: false);
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

    private async Task OnRequestCreateFeatureAsync(string name)
    {
        _createFeatureInitialName = name;
        _createFeatureWorkspaceBranch = await ResolveCurrentWorkspaceBranchNameAsync();
        _createFeatureModalVisible = true;
    }

    /// <summary>Unified branch across special Workspace repos (Features base on Current Workspace, not the viewed Feature).</summary>
    private async Task<string?> ResolveCurrentWorkspaceBranchNameAsync()
    {
        try
        {
            var snapshots = await LinkListQueryService.GetAllSnapshotsAsync(WorkspaceId, null, isSpecialWorkspace: true);
            var links = snapshots.Select(WorkspaceRepositoryLinkListMapper.ToLink).ToList();
            return GetUnifiedWorkspaceCurrentBranch(links);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not resolve Current Workspace branch for Create Feature modal");
            return null;
        }
    }

    private Task OnRemoveFeatureAsync()
    {
        if (_isFeatureContext && _selectedContextId is WorkspaceFeatureContextId ctx)
            BeginRemoveFeatureAnalysis(ctx);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The "-" icon on a Feature row inside the context-switcher dropdown (WorkspaceFeatureSelector): opens
    /// Remove Feature for that specific Feature, regardless of which context is currently selected/viewed.
    /// </summary>
    private Task OnRequestRemoveFeatureFromSelectorAsync(WorkspaceFeatureContextId contextId)
    {
        BeginRemoveFeatureAnalysis(contextId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A branch in the Switch Branch dialog was owned by a Feature worktree (§28A): rather than attempting an
    /// ordinary git branch delete (which the worktree would reject anyway), route straight to Remove Feature
    /// for that Feature's own context.
    /// </summary>
    private Task OnRequestFeatureCleanupFromBranchModalAsync(WorkspaceFeatureContextId featureContextId)
    {
        BeginRemoveFeatureAnalysis(featureContextId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs remove analysis under the page LoadingOverlay/terminal, then opens the confirmation dialog
    /// with the finished plan (no in-dialog "Checking..." spinner).
    /// </summary>
    private void BeginRemoveFeatureAnalysis(WorkspaceFeatureContextId contextId)
    {
        if (IsJobRunning)
            return;

        JobService.StartJob(PageJobKey, "Checking feature status...", async (job, ct) =>
        {
            try
            {
                var progress = new Progress<OperationProgress>(p =>
                {
                    if (p.Completed is int done && p.Total is int total && total > 0)
                        job.ReportProgress($"Checked {done} of {total}");
                    else if (!string.IsNullOrWhiteSpace(p.Message))
                        job.ReportProgress(p.Message);
                });

                var plan = await ScopedExecutor.ExecuteAsync<IWorkspaceFeatureOperations, RemoveFeaturePlan>(
                    svc => svc.AnalyzeRemoveFeatureAsync(contextId, ct, progress));

                if (!plan.Success)
                {
                    SafeInvoke(() => ToastService.ShowError(plan.Error ?? "Failed to prepare Feature removal."));
                    return;
                }

                await InvokeAsync(() =>
                {
                    if (_disposed) return;
                    _removeFeatureContextId = contextId;
                    _removeFeaturePlan = plan;
                    _removeFeatureModalVisible = true;
                    StateHasChanged();
                });
            }
            catch (OperationCanceledException)
            {
                SafeInvoke(() => ToastService.Show("Feature status check cancelled."));
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error analyzing Feature removal for context {ContextId}", contextId.Value);
                SafeInvoke(() => ToastService.ShowError("Failed to prepare Feature removal."));
                throw;
            }
        });
    }

    private Task OnRemoveFeatureCancelAsync()
    {
        _removeFeatureModalVisible = false;
        _removeFeaturePlan = null;
        _removeFeatureContextId = null;
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
        _removeFeaturePlan = null;

        if (removedCurrentContext)
        {
            try
            {
                var special = await FeatureContextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(WorkspaceId);
                await SelectedFeatureContextService.SetSelectedAsync(WorkspaceId, special);
                await OnSelectedContextChangedAsync(special);
                var path = new Uri(NavigationManager.Uri).GetLeftPart(UriPartial.Path);
                NavigationManager.NavigateTo(path, replace: true);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("was not found", StringComparison.Ordinal))
            {
                // The Feature context is already gone after a successful remove; a follow-up load that
                // still targets it must not surface as a remove-dialog error.
                Logger.LogDebug(ex, "Post-remove navigation ignored expected missing Feature context.");
            }
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

    private async Task LoadClientPreferencesAsync()
    {
        try
        {
            var storedMode = await JSRuntime.InvokeAsync<string?>("graymoonStorageGet", SyncModeStorageKey);
            _quickFetchIsPrimary = storedMode == "quick-fetch";
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
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

        // Await context + header + grid before first paint (prerender is off) so the header chrome
        // and column layout appear once in their final state — no Branch↔Create PR or column jump.
        var contextChanged = _boundContextQuery != ContextQuery;
        if (_loadedWorkspaceId == WorkspaceId && workspace != null && hasLoadedOnce && !contextChanged)
            return;

        if (_loadedWorkspaceId != WorkspaceId)
        {
            if (_loadedWorkspaceId != 0)
            {
                CancelBackgroundWork();
                await DetachVirtualScrollAsync();
                StopPrPollingLoop();
                _prPollingStarted = false;
            }

            _loadedWorkspaceId = WorkspaceId;
            errorMessage = null;
            hasLoadedOnce = false;
            // Drop the previous workspace name so the selector shows a placeholder until the new
            // header is read — never the generic "Workspace" fallback.
            workspace = null;
            ClearGridState();
        }

        if (contextChanged && workspace != null && hasLoadedOnce)
        {
            _boundContextQuery = ContextQuery;
            await SyncContextFromQueryAsync();
            return;
        }

        // Header first so WorkspaceName is ready before the feature selector resolves its options.
        await LoadWorkspaceHeaderAsync();
        await ResolveSelectedContextAsync();
        _boundContextQuery = BoundContextQueryFromSelection();
        await LoadClientPreferencesAsync();
        await LoadPendingRestoreScrollTopAsync();
        if (workspace == null)
        {
            isInitialLoading = false;
            return;
        }

        try
        {
            isInitialLoading = true;
            errorMessage = null;
            CancelBackgroundWork();
            _backgroundWorkCts = new CancellationTokenSource();
            await ResetAndLoadFromTopAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading workspace {WorkspaceId}", WorkspaceId);
            SetPageError("Failed to load workspace. Please try again later.");
            ClearGridState();
        }
        finally
        {
            isInitialLoading = false;
        }

        ApplySyncStateFromLoadedItems();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await OnAfterRenderRealtimeAsync(firstRender);

        if (!_prPollingStarted && hasLoadedOnce && !_disposed)
        {
            _prPollingStarted = true;
            StartPrPollingLoop();
        }

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
