using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.Application.Features;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions : IDisposable
{
    [Parameter] public int WorkspaceId { get; set; }

    /// <summary>Prefills the repository/workflow search from <c>?q=</c> (e.g. the merge-dialog checks link uses <c>repo:Name</c>).</summary>
    [SupplyParameterFromQuery(Name = "q")]
    public string? SearchQuery { get; set; }

    [SupplyParameterFromQuery(Name = "context")]
    public int? ContextQuery { get; set; }

    private WorkspaceFeatureContextId? _selectedContextId;
    private bool _isFeatureContext;

    [Inject] private WorkspaceActionService ActionService { get; set; } = null!;
    [Inject] private GitHubActionsService GitHubActionsService { get; set; } = null!;
    [Inject] private WorkspaceRepository WorkspaceRepository { get; set; } = null!;
    [Inject] private IOptions<WorkspaceOptions> WorkspaceOptions { get; set; } = null!;
    [Inject] private IServiceScopeFactory ServiceScopeFactory { get; set; } = null!;
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private ILogger<WorkspaceActions> Logger { get; set; } = null!;
    [Inject] private AppActivityStateService ActivityStateService { get; set; } = null!;
    [Inject] private WorkspaceContextNavigationService ContextNavigation { get; set; } = null!;
    [Inject] private IWorkspaceFeatureContextResolver FeatureContextResolver { get; set; } = null!;
    [Inject] private GrayMoon.App.Services.Queries.IWorkspaceRepositoryLinkListQueryService LinkListQueryService { get; set; } = null!;

    private int MaxConcurrency => Math.Max(1, WorkspaceOptions.Value.MaxParallelOperations);

    protected override async Task OnInitializedAsync()
    {
        ApplyIncomingSearchQuery();
        ActivityStateService.BecameActive += OnActivityBecameActive;
        var info = await ContextNavigation.ResolveForPageAsync(WorkspaceId, ContextQuery);
        _selectedContextId = info.ContextId;
        _isFeatureContext = !info.IsSpecialWorkspace;
        await LoadWorkspaceAsync();
    }

    protected override void OnParametersSet() => ApplyIncomingSearchQuery();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        if (!isLoading && rows.Count > 0)
            StartBackgroundRefresh();

        if (workspace != null && errorMessage == null)
        {
            _hubConnection = WorkspaceSyncHubConnectionHelper.Create(NavigationManager);

            _hubConnection.On<int>("WorkspaceSynced", async (workspaceId) =>
            {
                if (workspaceId != WorkspaceId) return;

                _syncDebounceCts?.Cancel();
                _syncDebounceCts?.Dispose();
                _syncDebounceCts = new CancellationTokenSource();
                var cts = _syncDebounceCts;
                try
                {
                    await Task.Delay(SyncDebounceMs, cts.Token);
                    await InvokeAsync(RefreshFromSyncAsync);
                }
                catch (OperationCanceledException) { /* debounced */ }
                finally
                {
                    if (cts == _syncDebounceCts)
                    {
                        _syncDebounceCts?.Dispose();
                        _syncDebounceCts = null;
                    }
                }
            });

            _hubConnection.On<int, int>("RepositorySynced", async (workspaceId, repositoryId) =>
            {
                if (workspaceId != WorkspaceId) return;

                lock (_pendingRepositorySyncIds)
                    _pendingRepositorySyncIds.Add(repositoryId);

                _repositorySyncDebounceCts?.Cancel();
                _repositorySyncDebounceCts?.Dispose();
                _repositorySyncDebounceCts = new CancellationTokenSource();
                var cts = _repositorySyncDebounceCts;
                try
                {
                    await Task.Delay(SyncDebounceMs, cts.Token);
                    await InvokeAsync(RefreshFromRepositorySyncAsync);
                }
                catch (OperationCanceledException) { /* debounced */ }
                finally
                {
                    if (cts == _repositorySyncDebounceCts)
                    {
                        _repositorySyncDebounceCts?.Dispose();
                        _repositorySyncDebounceCts = null;
                    }
                }
            });

            await _hubConnection.StartAsync();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        ActivityStateService.BecameActive -= OnActivityBecameActive;
        _cts.Cancel();
        _cts.Dispose();
        _syncDebounceCts?.Cancel();
        _syncDebounceCts?.Dispose();
        _repositorySyncDebounceCts?.Cancel();
        _repositorySyncDebounceCts?.Dispose();
        lock (_pendingRepositorySyncIds)
            _pendingRepositorySyncIds.Clear();
        WorkspaceSyncHubConnectionHelper.DisposeFireAndForget(_hubConnection);
    }
    private async Task OnSelectedContextChangedAsync(WorkspaceFeatureContextId contextId)
    {
        var info = await FeatureContextResolver.GetRequiredAsync(contextId, WorkspaceId);
        _selectedContextId = info.ContextId;
        _isFeatureContext = !info.IsSpecialWorkspace;
        await LoadWorkspaceAsync();
    }
}
