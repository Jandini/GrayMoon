using GrayMoon.App.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories : IDisposable
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

    protected override async Task OnInitializedAsync()
    {
        AgentQueueStateService.OnQueueStateChanged(OnQueueStateChanged);
        JobService.Changed += OnJobServiceChanged;
        _wasJobRunning = IsJobRunning;
        await LoadWorkspaceAsync();
        ApplySyncStateFromWorkspace();
    }

    public void Dispose()
    {
        _disposed = true;
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
        _loadMismatchedDepsLock.Dispose();
    }
}
