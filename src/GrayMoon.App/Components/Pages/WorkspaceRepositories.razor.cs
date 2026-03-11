using System.Text.Json;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories : IDisposable
{
    [Parameter] public int WorkspaceId { get; set; }

    private Workspace? workspace;
    private List<WorkspaceRepositoryLink> workspaceRepositories = new();
    private IReadOnlyDictionary<int, PullRequestInfo?> prByRepositoryId = new Dictionary<int, PullRequestInfo?>();
    private IReadOnlyDictionary<int, RepoGitVersionInfo> repoGitInfos = new Dictionary<int, RepoGitVersionInfo>();
    private string? errorMessage;
    private bool isLoading = true;
    private bool isSyncing = false;
    private string syncProgressMessage = "Synchronizing...";
    private bool isUpdating = false;
    private string updateProgressMessage = "Updating dependencies...";
    private CancellationTokenSource? _updateCts;
    private bool isPushing = false;
    private string pushProgressMessage = "Pushing...";
    private CancellationTokenSource? _pushCts;
    private bool showPushWithDependenciesModal = false;
    private PushDependencyInfoForRepo? pushWithDependenciesInfo = null;
    private IReadOnlySet<int>? pushWithDependenciesRepoIdsToPush = null;
    private int pushWithDependenciesRepoId;
    private string? pushWithDependenciesBranchName;
    private string? pushWithDependenciesRepoName;
    private IReadOnlySet<int>? pushPlanRepoIds = null;
    private bool? isOutOfSync = null;
    private bool hasUnmatchedDependencies => workspaceRepositories.Any(wr => (wr.UnmatchedDeps ?? 0) > 0);
    private bool isPushRecommended => workspaceRepositories.Any(wr => (wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false);
    /// <summary>When true, any repository has incoming commits; header shows red Pull button and executes only Pull (commit sync).</summary>
    private bool hasIncomingCommits => workspaceRepositories.Any(wr => (wr.IncomingCommits ?? 0) > 0);
    private IEnumerable<IGrouping<int?, WorkspaceRepositoryLink>> LevelGroups =>
        workspaceRepositories
            .GroupBy(wr => wr.DependencyLevel)
            .OrderByDescending(g => g.Key ?? int.MinValue);

    private List<WorkspaceRepositoryLink> FilteredWorkspaceRepositories => GetFilteredWorkspaceRepositories();

    private string ApiBaseUrl => NavigationManager.BaseUri.TrimEnd('/');

    private IEnumerable<IGrouping<int?, WorkspaceRepositoryLink>> FilteredLevelGroups =>
        FilteredWorkspaceRepositories
            .GroupBy(wr => wr.DependencyLevel)
            .OrderByDescending(g => g.Key ?? int.MinValue);
    private Dictionary<int, RepoSyncStatus> repoSyncStatus = new();
    private CancellationTokenSource? _syncCts;
    private IReadOnlyDictionary<int, IReadOnlyList<(string PackageId, string CurrentVersion, string NewVersion)>> _mismatchedDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<(string, string, string)>>();
    private bool isCommitSyncing = false;
    private string commitSyncProgressMessage = "Synchronizing commits...";
    private CancellationTokenSource? _commitSyncCts;
    private bool isCheckingOut = false;
    private string checkoutProgressMessage = "Checking out branch...";
    private CancellationTokenSource? _checkoutCts;
    private Dictionary<int, string> repositoryErrors = new(); // repositoryId -> error message
    private HashSet<string> clickedVersions = new(); // Track clicked versions to hide hover until mouse leaves
    private HashSet<int> clickedDependencyBadges = new(); // Track clicked dependency badges to hide tooltip immediately
    private HubConnection? _hubConnection;
    private bool showRepositoriesModal;
    private bool isSavingRepositories;
    private bool isPersisting;
    private bool hasConnectors;
    private string? repositoriesModalErrorMessage;
    private List<GitHubRepositoryEntry>? repositoriesModalRepositories;
    private HashSet<int> selectedRepositoryIds = new();
    private int? fetchedRepositoryCount;
    private CancellationTokenSource? _fetchRepositoriesCts;
    private bool showSwitchBranchModal = false;
    private int switchBranchRepositoryId;
    private string? switchBranchRepositoryName;
    private string? switchBranchCurrentBranch;
    private string? switchBranchRepositoryUrl;
    private bool showBranchModal = false;
    private IReadOnlyList<string> branchModalCommonBranches = Array.Empty<string>();
    private string branchModalDefaultDisplayText = "multiple";
    private bool isCreatingBranches = false;
    private string createBranchesProgressMessage = "Creating branches...";
    private CancellationTokenSource? _createBranchesCts;
    private bool isCreatingBranch = false;
    private string createBranchMessage = "";
    private bool isSyncingToDefault = false;
    private string syncToDefaultMessage = "";
    private IReadOnlyList<(int RepoId, int? DefaultAhead, bool? HasUpstream)>? _syncToDefaultCheckResults = null;
    private bool showUpdateModal = false;
    private bool showUpdateSingleRepoDependenciesModal = false;
    private SyncDependenciesRepoPayload? updateSingleRepoDependenciesPayload = null;
    private int updateSingleRepoDependenciesRepositoryId;
    private string? updateSingleRepoDependenciesRepoName;
    private bool updatePlanIsMultiLevel = false;
    private bool showVersionFilesCommitModal = false;
    private string versionFilesCommitMessage = "";
    private IReadOnlyList<string> versionFilesCommitList = Array.Empty<string>();
    private IReadOnlyList<(int RepoId, string RepoName, IReadOnlyList<string> FilePaths)>? versionFilesByRepoToCommit;
    /// <summary>Cached update plan payload from the last header-level Update click. Used to know which repos had dependency updates when offering post-update commits.</summary>
    private IReadOnlyList<SyncDependenciesRepoPayload>? _updatePlanPayloadForUpdateOnly = null;
    private bool showConfirmModal = false;
    private string confirmModalMessage = "";
    private string confirmModalButtonText = "Yes";
    private Func<Task>? _pendingConfirmAction;
    private string searchTerm = string.Empty;

    private const int RefreshDebounceMs = 200;
    private CancellationTokenSource? _refreshDebounceCts;

    protected override async Task OnInitializedAsync()
    {
        await LoadWorkspaceAsync();
        ApplySyncStateFromWorkspace();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try { await JSRuntime.InvokeVoidAsync("focusElement", "workspace-repos-search"); } catch { /* ignore */ }
        }
        if (firstRender && workspace != null && errorMessage == null)
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(NavigationManager.ToAbsoluteUri("/hubs/workspace-sync"))
                .WithAutomaticReconnect()
                .Build();
            _hubConnection.On<int>("WorkspaceSynced", async (workspaceId) =>
            {
                if (workspaceId != WorkspaceId) return;
                _refreshDebounceCts?.Cancel();
                _refreshDebounceCts?.Dispose();
                _refreshDebounceCts = new CancellationTokenSource();
                var cts = _refreshDebounceCts;
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
                    if (cts == _refreshDebounceCts)
                    {
                        _refreshDebounceCts?.Dispose();
                        _refreshDebounceCts = null;
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

    private void ApplySyncStateFromWorkspace()
    {
        if (workspace == null || workspaceRepositories.Count == 0)
        {
            return;
        }
        repoSyncStatus = workspaceRepositories
            .Where(wr => wr.Repository != null)
            .ToDictionary(wr => wr.RepositoryId, wr => wr.SyncStatus);
        isOutOfSync = repoSyncStatus.Values.Any(s => s != RepoSyncStatus.InSync);
        StateHasChanged();
    }

    public void Dispose()
    {
        _refreshDebounceCts?.Cancel();
        _refreshDebounceCts?.Dispose();
        _refreshDebounceCts = null;
        _ = _hubConnection?.StopAsync();
        _hubConnection?.DisposeAsync();
        _syncCts?.Cancel();
        _syncCts?.Dispose();
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _pushCts?.Cancel();
        _pushCts?.Dispose();
        _commitSyncCts?.Cancel();
        _commitSyncCts?.Dispose();
        _fetchRepositoriesCts?.Cancel();
        _fetchRepositoriesCts?.Dispose();
        _createBranchesCts?.Cancel();
        _createBranchesCts?.Dispose();
    }

    /// <summary>Called when WorkspaceSynced is received (or after Update): reload from a fresh scope so the grid gets current DB values (no stale DbContext).</summary>
    private async Task RefreshFromSync()
    {
        if (isSyncing || isUpdating)
            return;
        await ReloadWorkspaceDataFromFreshScopeAsync();
        ApplySyncStateFromWorkspace();
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadWorkspaceAsync()
    {
        try
        {
            isLoading = true;
            errorMessage = null;
            await ReloadWorkspaceDataAsync();
            _ = RefreshPullRequestsInBackgroundAndReloadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Failed to load workspace. Please try again later.";
            workspaceRepositories = new List<WorkspaceRepositoryLink>();
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task ReloadWorkspaceDataAsync()
    {
        workspace = await WorkspaceRepository.GetByIdAsync(WorkspaceId);
        if (workspace == null)
        {
            errorMessage = "Workspace not found.";
            workspaceRepositories = new List<WorkspaceRepositoryLink>();
            return;
        }

        workspaceRepositories = workspace.Repositories
            .OrderByDescending(wr => wr.DependencyLevel ?? int.MinValue)
            .ThenByDescending(wr => wr.Dependencies ?? int.MinValue)
            .ToList();
        prByRepositoryId = BuildPrByRepositoryIdFromLinks(workspaceRepositories);
        await LoadMismatchedDependencyLinesAsync();
    }

    private static IReadOnlyDictionary<int, PullRequestInfo?> BuildPrByRepositoryIdFromLinks(List<WorkspaceRepositoryLink> links)
    {
        var dict = new Dictionary<int, PullRequestInfo?>();
        foreach (var wr in links.Where(wr => wr.PullRequest != null))
            dict[wr.RepositoryId] = wr.PullRequest!.PullRequestNumber.HasValue ? wr.PullRequest.ToPullRequestInfo() : null;
        return dict;
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
            await WorkspacePullRequestService.RefreshPullRequestsForWorkspaceAsync(WorkspaceId);
            await ReloadWorkspaceDataAsync();

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

    /// <summary>Calls the same branch refresh API as Switch Branch Fetch (fetch + persist) for the given repo.</summary>
    private async Task RefreshBranchesForRepositoryAsync(int repositoryId)
    {
        try
        {
            var httpClient = HttpClientFactory.CreateClient();
            var request = new { workspaceId = WorkspaceId, repositoryId };
            var json = JsonSerializer.Serialize(request);
            var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var baseUrl = NavigationManager.BaseUri.TrimEnd('/');
            var response = await httpClient.PostAsync($"{baseUrl}/api/branches/refresh", content);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                Logger.LogDebug("Branch refresh after PR merge failed for RepositoryId={RepositoryId}: {StatusCode}, {Error}", repositoryId, response.StatusCode, errorText);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Branch refresh after PR merge failed for RepositoryId={RepositoryId}", repositoryId);
        }
    }

    /// <summary>Reload workspace after abort/cancel using a fresh scope. Swallows ObjectDisposedException and InvalidOperationException so abort does not cascade errors when the circuit or context is already disposed.</summary>
    private async Task ReloadWorkspaceDataAfterCancelAsync()
    {
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
        ApplySyncStateFromWorkspace();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Loads workspace using a new scope (fresh DbContext) so we get current DB values and avoid EF cache. Used by RefreshFromSync so the grid shows updated UnmatchedDeps after notify or Update.</summary>
    private async Task ReloadWorkspaceDataFromFreshScopeAsync()
    {
        await using var scope = ServiceScopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<WorkspaceRepository>();
        var w = await repo.GetByIdAsync(WorkspaceId);
        if (w == null)
        {
            errorMessage = "Workspace not found.";
            workspaceRepositories = new List<WorkspaceRepositoryLink>();
            return;
        }
        workspace = w;
        workspaceRepositories = workspace.Repositories
            .OrderByDescending(wr => wr.DependencyLevel ?? int.MinValue)
            .ThenByDescending(wr => wr.Dependencies ?? int.MinValue)
            .ToList();
        prByRepositoryId = BuildPrByRepositoryIdFromLinks(workspaceRepositories);
        await LoadMismatchedDependencyLinesAsync();
    }

    private async Task LoadMismatchedDependencyLinesAsync()
    {
        if (!workspaceRepositories.Any(wr => (wr.UnmatchedDeps ?? 0) > 0))
        {
            _mismatchedDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<(string, string, string)>>();
            return;
        }
        try
        {
            var (payloads, _) = await WorkspaceGitService.GetUpdatePlanAsync(WorkspaceId);
            var dict = new Dictionary<int, IReadOnlyList<(string PackageId, string CurrentVersion, string NewVersion)>>();
            foreach (var p in payloads ?? Array.Empty<SyncDependenciesRepoPayload>())
            {
                var lines = p.ProjectUpdates
                    .SelectMany(pu => pu.PackageUpdates)
                    .GroupBy(x => (x.PackageId.Trim(), x.CurrentVersion.Trim(), x.NewVersion.Trim()))
                    .Select(g => (g.Key.Item1, g.Key.Item2, g.Key.Item3))
                    .ToList();
                dict[p.RepoId] = lines;
            }
            _mismatchedDependencyLinesByRepo = dict;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not load mismatched dependency lines for workspace {WorkspaceId}", WorkspaceId);
            _mismatchedDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<(string, string, string)>>();
        }
    }

    private void OnSearchChanged(ChangeEventArgs e)
    {
        searchTerm = e.Value?.ToString() ?? string.Empty;
        StateHasChanged();
    }

    private void AbortSyncAsync()
    {
        _syncCts?.Cancel();
    }

    private void AbortUpdateAsync()
    {
        _updateCts?.Cancel();
    }

    private void SetSyncProgress(string message)
    {
        syncProgressMessage = message;
        _ = InvokeAsync(StateHasChanged);
    }

    private void SetUpdateProgress(string message)
    {
        updateProgressMessage = message;
        _ = InvokeAsync(StateHasChanged);
    }

    private void SetPushProgress(string message)
    {
        _ = InvokeAsync(() =>
        {
            pushProgressMessage = message;
            StateHasChanged();
        });
    }

    private void SetCommitSyncProgress(string message)
    {
        commitSyncProgressMessage = message;
        _ = InvokeAsync(StateHasChanged);
    }

    private void SetCheckoutProgress(string message)
    {
        checkoutProgressMessage = message;
        _ = InvokeAsync(StateHasChanged);
    }

    private void SetCreateBranchesProgress(string message)
    {
        createBranchesProgressMessage = message;
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Attempts to update all configured file versions at the end of an update workflow.
    /// Shows a toast for each file that was updated; logs warnings on failure and skips gracefully when no files are configured.
    /// </summary>
    private async Task TryUpdateFileVersionsAsync()
    {
        try
        {
            var (_, _, error, _) = await FileVersionService.UpdateAllVersionsAsync(
                WorkspaceId,
                selectedRepositoryIds: null,
                onFileUpdated: filePath => ToastService.Show($"File {filePath} updated."));
            if (error != null && !error.Contains("No version configurations"))
                Logger.LogWarning("Auto file-version update after dependency update: {Error}", error);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Auto file-version update failed after dependency update for workspace {WorkspaceId}", WorkspaceId);
        }
    }

    /// <summary>Runs file version update without toasts; returns the list of updated files (repo, path) for commit or confirm dialog. Caller handles commit vs dialog.</summary>
    private async Task<IReadOnlyList<(int RepositoryId, string RepoName, string FilePath)>?> RunFileVersionUpdateAndGetUpdatedFilesAsync()
    {
        try
        {
            var (_, _, error, updatedFiles) = await FileVersionService.UpdateAllVersionsAsync(
                WorkspaceId,
                selectedRepositoryIds: null,
                onFileUpdated: null,
                cancellationToken: default);
            if (error != null && !error.Contains("No version configurations"))
                Logger.LogWarning("File-version update: {Error}", error);
            return updatedFiles?.Count > 0 ? updatedFiles : null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "File-version update failed for workspace {WorkspaceId}", WorkspaceId);
            return null;
        }
    }

    private void AbortPushAsync()
    {
        _pushCts?.Cancel();
    }

    private void AbortCommitSyncAsync()
    {
        _commitSyncCts?.Cancel();
    }

    private void CloseUpdateModal()
    {
        showUpdateModal = false;
        StateHasChanged();
    }

    private void CloseConfirmModal()
    {
        showConfirmModal = false;
        confirmModalButtonText = "Yes";
        _pendingConfirmAction = null;
        StateHasChanged();
    }

    private async Task OnConfirmModalYesAsync()
    {
        var action = _pendingConfirmAction;
        CloseConfirmModal();
        if (action != null)
            await action();
    }

    private void ShowConfirm(string message, Func<Task> onConfirm, string confirmButtonText = "Yes")
    {
        confirmModalMessage = message;
        confirmModalButtonText = confirmButtonText;
        _pendingConfirmAction = onConfirm;
        showConfirmModal = true;
        StateHasChanged();
    }
}


