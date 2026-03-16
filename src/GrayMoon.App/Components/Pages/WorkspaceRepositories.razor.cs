using System.Text.Json;
using GrayMoon.App.Models;
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
    private readonly Dictionary<int, DateTime> _lastPrRefreshByRepoId = new();
    private static readonly TimeSpan PrRefreshThrottle = TimeSpan.FromSeconds(10);
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
    private bool HasSearchFilter => !string.IsNullOrWhiteSpace(searchTerm);
    private string PageTitleText => workspace?.Name ?? "Workspace";
    private string RepositoriesModalTitle => $"Repositories for {PageTitleText}";
    private bool ShowRepositoriesFetchOverlay => _repositoriesModal.IsVisible && _repositoriesModal.IsFetching;
    private string RepositoriesFetchOverlayMessage => _repositoriesModal.FetchedRepositoryCount is null || _repositoriesModal.FetchedRepositoryCount == 0
        ? "Fetching repositories..."
        : $"Fetched {_repositoriesModal.FetchedRepositoryCount} {(_repositoriesModal.FetchedRepositoryCount == 1 ? "repository" : "repositories")}";
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
    private CancellationTokenSource? _fetchRepositoriesCts;
    private RepositoriesModalState _repositoriesModal = new();
    private SwitchBranchModalState _switchBranchModal = new();
    private BranchModalState _branchModal = new();
    private bool isCreatingBranches = false;
    private string createBranchesProgressMessage = "Creating branches...";
    private CancellationTokenSource? _createBranchesCts;
    private bool isCreatingBranch = false;
    private string createBranchMessage = "";
    private bool isSyncingToDefault = false;
    private string syncToDefaultMessage = "";
    private IReadOnlyList<(int RepoId, int? DefaultAhead, bool? HasUpstream)>? _syncToDefaultCheckResults = null;
    private UpdateModalState _updateModal = new();
    private UpdateSingleRepoDependenciesModalState _updateSingleRepoModal = new();
    private VersionFilesCommitModalState _versionFilesCommitModal = new();
    private PushWithDependenciesModalState _pushWithDependenciesModal = new();
    private ConfirmModalState _confirmModal = new();
    private string searchTerm = string.Empty;

    private bool _syncAwaitingAgentTasks;
    private bool _pushAwaitingAgentTasks;
    private bool _updateAwaitingAgentTasks;
    private bool _commitSyncAwaitingAgentTasks;
    private bool _syncToDefaultAwaitingAgentTasks;
    private bool _creatingBranchesAwaitingAgentTasks;

    private int AgentTasksPendingCount => AgentQueueStateService.GetPendingCountForWorkspace(WorkspaceId);

    private const int RefreshDebounceMs = 200;
    private CancellationTokenSource? _refreshDebounceCts;

    protected override async Task OnInitializedAsync()
    {
        AgentQueueStateService.OnQueueStateChanged(OnQueueStateChanged);
        await LoadWorkspaceAsync();
        ApplySyncStateFromWorkspace();
    }

    private void OnQueueStateChanged(object? sender, EventArgs e) => _ = InvokeAsync(StateHasChanged);

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
        AgentQueueStateService.RemoveQueueStateChanged(OnQueueStateChanged);
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
        workspace = await WorkspacePageService.WorkspaceRepository.GetByIdAsync(WorkspaceId);
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
            await WorkspacePageService.WorkspacePullRequestService.RefreshPullRequestsForWorkspaceAsync(WorkspaceId);
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
            await ReloadWorkspaceDataFromFreshScopeAsync();
            ApplySyncStateFromWorkspace();
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "PR refresh on badge enter failed for RepositoryId={RepositoryId}", repositoryId);
        }
    }

    /// <summary>Calls the same branch refresh API as Switch Branch Fetch (fetch + persist) for the given repo.</summary>
    private async Task RefreshBranchesForRepositoryAsync(int repositoryId)
    {
        try
        {
            var httpClient = WorkspacePageService.HttpClientFactory.CreateClient();
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
            var (payloads, _) = await WorkspacePageService.WorkspaceGitService.GetUpdatePlanAsync(WorkspaceId);
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

    /// <summary>Returns \"Waiting for x agent jobs\" when overlay is visible, workflow set the awaiting flag, and agent jobs are pending; otherwise returns progressMessage.</summary>
    private string GetOverlayMessage(string progressMessage, bool overlayVisible, bool awaitingAgentTasks)
    {
        if (!overlayVisible) return progressMessage;
        if (!awaitingAgentTasks) return progressMessage;
        if (AgentTasksPendingCount == 0) return progressMessage;
        return AgentTasksPendingCount == 1
            ? "Waiting for 1 agent job"
            : $"Waiting for {AgentTasksPendingCount} agent jobs";
    }

    /// <summary>
    /// Called by the dependency update orchestrator when version-file updates have been applied but not yet committed.
    /// Groups files by repo and opens the version-files commit modal so the user can confirm and commit via CommitFileVersionUpdatesAsync.
    /// </summary>
    private void HandleVersionFilesUpdated(IReadOnlyList<(int RepoId, string RepoName, IReadOnlyList<string> FilePaths)> byRepo)
    {
        if (byRepo == null || byRepo.Count == 0)
            return;

        var repoCount = byRepo.Count;
        var distinctFiles = byRepo
            .SelectMany(r => r.FilePaths)
            .Distinct()
            .ToList();
        var filesForDisplay = distinctFiles.Count <= 5
            ? distinctFiles
            : distinctFiles.Take(5).Concat(new[] { $"... and {distinctFiles.Count - 5} more" }).ToList();
        var prefix = repoCount == 1
            ? "Commit the updated version files in this repository?"
            : $"Commit all updated version files in all {repoCount} repositories?";

        OpenVersionFilesCommitModal(prefix, byRepo, filesForDisplay);
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
        _updateModal = _updateModal with { IsVisible = false };
        StateHasChanged();
    }

    private void CloseConfirmModal()
    {
        _confirmModal = _confirmModal with
        {
            IsVisible = false,
            ButtonText = "Yes",
            PendingAction = null,
        };
        StateHasChanged();
    }

    private async Task OnConfirmModalYesAsync()
    {
        var action = _confirmModal.PendingAction;
        CloseConfirmModal();
        if (action != null)
            await action();
    }

    private void ShowConfirm(string message, Func<Task> onConfirm, string confirmButtonText = "Yes")
    {
        _confirmModal = _confirmModal with
        {
            IsVisible = true,
            Message = message,
            ButtonText = confirmButtonText,
            PendingAction = onConfirm,
        };
        StateHasChanged();
    }


    private void ShowConfirmOpenPr(IEnumerable<WorkspaceRepositoryLink> group)
    {
        const int confirmThreshold = 10;
        var toOpen = new List<string>();
        foreach (var wr in group)
        {
            if (wr.Repository == null || string.IsNullOrWhiteSpace(wr.BranchName)) continue;
            var repoUrl = RepositoryUrlHelper.GetRepositoryUrl(wr.Repository.CloneUrl);
            if (string.IsNullOrEmpty(repoUrl)) continue;

            var hasOpenPr = wr.PullRequest != null && string.Equals(wr.PullRequest.State, "open", StringComparison.OrdinalIgnoreCase);
            if (hasOpenPr && !string.IsNullOrEmpty(wr.PullRequest!.HtmlUrl))
            {
                toOpen.Add(wr.PullRequest.HtmlUrl);
            }
            else if ((wr.DefaultBranchAheadCommits ?? 0) > 0)
            {
                if (!string.IsNullOrWhiteSpace(wr.DefaultBranchName))
                {
                    toOpen.Add($"{repoUrl}/compare/{wr.DefaultBranchName}...{Uri.EscapeDataString(wr.BranchName)}");
                }
            }
        }
        if (toOpen.Count == 0)
        {
            ToastService.Show("No repositories found that have an open pull request or ahead commits to open in GitHub.");
            return;
        }
        async Task OpenPrAsync()
        {
            await JSRuntime.InvokeVoidAsync("graymoonOpenUrls", toOpen);
        }
        if (toOpen.Count < confirmThreshold)
            _ = OpenPrAsync();
        else
            ShowConfirm($"Do you want to open pull request for {toOpen.Count} repositories?", OpenPrAsync);
    }

    private void ShowConfirmSyncCommitsLevel(List<int> repositoryIds)
    {
        if (repositoryIds.Count <= 1)
            _ = CommitSyncLevelAsync(repositoryIds);
        else
            ShowConfirm($"Do you want to sync commits for {repositoryIds.Count} repositories?", () => CommitSyncLevelAsync(repositoryIds));
    }

    private void ShowConfirmSyncLevel(List<int> repositoryIds)
    {
        const int confirmThreshold = 10;
        if (repositoryIds.Count < confirmThreshold)
            _ = SyncLevelAsync(repositoryIds);
        else
            ShowConfirm($"Do you want to sync {repositoryIds.Count} repositories in this level?", () => SyncLevelAsync(repositoryIds));
    }

    private void ShowConfirmSyncToDefaultLevel(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0)
            return;

        var nonDefaultRepoIds = repositoryIds
            .Select(id => workspaceRepositories.FirstOrDefault(w => w.RepositoryId == id))
            .Where(wr =>
            {
                if (wr == null || string.IsNullOrWhiteSpace(wr.BranchName))
                    return false;
                if (string.IsNullOrWhiteSpace(wr.DefaultBranchName))
                    return true;
                return !string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal);
            })
            .Select(wr => wr!.RepositoryId)
            .Distinct()
            .ToList();

        if (nonDefaultRepoIds.Count == 0)
        {
            ToastService.Show("All repositories in this level are already on the default branch.");
            return;
        }

        CheckBranchesAndConfirmSyncToDefaultLevel(nonDefaultRepoIds);
    }

    private void CheckBranchesAndConfirmSyncToDefaultLevel(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0 || isSyncingToDefault)
            return;

        _syncToDefaultCheckResults = null;

        try
        {
            // Use persisted workspace link state (updated by hooks); no agent GetCommitCounts call.
            var checkResults = repositoryIds
                .Select(repoId =>
                {
                    var wr = workspaceRepositories.FirstOrDefault(w => w.RepositoryId == repoId);
                    return (RepoId: repoId, DefaultAhead: wr?.DefaultBranchAheadCommits, HasUpstream: wr?.BranchHasUpstream);
                })
                .ToList();
            var safeRepoIds = checkResults
                .Where(r => (r.DefaultAhead ?? 0) == 0 || IsPrMergedForRepo(r.RepoId))
                .Select(r => r.RepoId)
                .ToList();
            var blocked = checkResults
                .Where(r => (r.DefaultAhead ?? 0) > 0 && !IsPrMergedForRepo(r.RepoId))
                .ToList();

            foreach (var r in blocked)
            {
                var name = workspaceRepositories.FirstOrDefault(w => w.RepositoryId == r.RepoId)?.Repository?.RepositoryName ?? r.RepoId.ToString();
                ToastService.Show($"{name}: skipped sync to default (commits ahead of default, PR not merged).");
            }

            if (safeRepoIds.Count == 0)
            {
                if (blocked.Count == 0)
                    ToastService.Show("No repositories to sync.");
                return;
            }

            _syncToDefaultCheckResults = checkResults.Where(r => safeRepoIds.Contains(r.RepoId)).ToList();
            var safeCount = safeRepoIds.Count;
            var message = safeCount == 1
                ? "This will checkout the default branch, remove the current branch locally, and pull the latest. If the branch had an upstream, the remote branch may be deleted. Uncommitted local changes can block checkout."
                : $"This will sync {safeCount} repositories to their default branch: checkout default, remove the current branch locally, and pull. If a branch had an upstream, the remote branch may be deleted. Uncommitted local changes can block checkout for that repo.";
            ShowConfirm(message, () => SyncToDefaultLevelAsync(safeRepoIds), "Proceed");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error checking branches for sync to default");
            ToastService.Show("Failed to prepare sync to default.");
        }
    }

    private void ShowConfirmOpenGitHub(int count, IReadOnlyList<string?> urls)
    {
        async Task OpenGitHubAsync()
        {
            var list = urls.Where(u => !string.IsNullOrEmpty(u)).Cast<string>().ToList();
            await JSRuntime.InvokeVoidAsync("graymoonOpenUrls", list);
        }
        if (count <= 1)
            _ = OpenGitHubAsync();
        else
            ShowConfirm($"Do you want to open GitHub page for {count} repositories?", OpenGitHubAsync);
    }

    private void HandleOpenGitHubFromLevel((int count, IReadOnlyList<string?> urls) args)
    {
        ShowConfirmOpenGitHub(args.count, args.urls);
    }

    /// <summary>
    /// True if the workspace has a PR for this repository's current branch that is either merged
    /// or closed (treated the same as merged for sync-to-default safety checks).
    /// </summary>
    private bool IsPrMergedForRepo(int repositoryId)
    {
        if (!prByRepositoryId.TryGetValue(repositoryId, out var pr) || pr == null)
            return false;

        if (pr.IsMerged)
            return true;

        return string.Equals(pr.State, "closed", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ShowConfirmUpdateDependenciesAsync(int repositoryId, int unmatchedCount)
    {
        if (workspace == null || isUpdating || isSyncing)
            return;
        try
        {
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            var (payload, _) = await workspaceGitService.GetUpdatePlanAsync(WorkspaceId, new HashSet<int> { repositoryId });
            if (payload == null || payload.Count == 0)
            {
                ToastService.Show("No dependency updates for this repository.");
                return;
            }
            var repoPayload = payload[0];
            var repoName = workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId)?.Repository?.RepositoryName;
            _updateSingleRepoModal = _updateSingleRepoModal with
            {
                IsVisible = true,
                Payload = repoPayload,
                RepositoryId = repositoryId,
                RepoName = repoName
            };
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting update plan for repository {RepositoryId}", repositoryId);
            ToastService.Show("Could not load dependency updates.");
        }
    }

    private void CloseUpdateSingleRepositoryDependenciesModal()
    {
        _updateSingleRepoModal = _updateSingleRepoModal with { IsVisible = false, Payload = null };
    }

    private void OnCommitDependencyProgress(int current, int total, int unused)
    {
        SetUpdateProgress($"Committed {current} of {total}");
    }

    private async Task OnUpdateSingleRepositoryDependenciesProceedAsync()
    {
        if (_updateSingleRepoModal.Payload == null)
            return;
        var repositoryId = _updateSingleRepoModal.RepositoryId;
        CloseUpdateSingleRepositoryDependenciesModal();

        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource();
        isUpdating = true;
        SetUpdateProgress("Updating repository...");
        repositoryErrors.Remove(repositoryId);
        try
        {
            await InvokeAsync(StateHasChanged);
            await using (var scope = ServiceScopeFactory.CreateAsyncScope())
            {
                var updateHandler = scope.ServiceProvider.GetRequiredService<WorkspaceUpdateHandler>();
                await updateHandler.RunUpdateAsync(
                    WorkspaceId,
                    _updateCts.Token,
                    SetUpdateProgress,
                    (repoId, msg) => { repositoryErrors[repoId] = msg; _ = InvokeAsync(StateHasChanged); },
                    onAppSideComplete: () => _updateAwaitingAgentTasks = true,
                    repoIdsToUpdate: new HashSet<int> { repositoryId },
                    onVersionFilesUpdated: HandleVersionFilesUpdated);
            }
            await RefreshFromSync();
        }
        catch (OperationCanceledException)
        {
            ToastService.Show("Update cancelled.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Update dependencies failed for repository {RepositoryId}", repositoryId);
            ToastService.Show(ex.Message);
        }
        finally
        {
            _updateAwaitingAgentTasks = false;
            isUpdating = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void ShowConfirmSyncCommits(int repositoryId)
    {
        ShowConfirm("Do you want to sync commits for this repository?", () => CommitSyncAsync(repositoryId));
    }

    /// <summary>When user clicks the Commits badge and there are incoming commits: run Pull (commit sync) only, with same merge/error handling as CommitSyncAsync.</summary>
    private void OnPullBadgeClickAsync(int repositoryId)
    {
        if (workspace == null || isCommitSyncing || isSyncing || isUpdating)
            return;
        ShowConfirm("Do you want to pull for this repository?", () => CommitSyncAsync(repositoryId));
    }

    /// <summary>When user clicks the not-upstreamed badge: check dependencies. Show modal only if at least one dependency repo needs push; otherwise push directly.</summary>
    private async Task OnPushBadgeClickAsync(int repositoryId, string? branchName)
    {
        if (workspace == null || isPushing || isSyncing || isUpdating)
            return;

        try
        {
            var repoIdsThatNeedPush = workspaceRepositories
                .Where(wr => (wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false)
                .Select(wr => wr.RepositoryId)
                .ToHashSet();
            var depInfo = await WorkspaceDependencyService.GetPushDependencyInfoForRepoAsync(
                WorkspaceId,
                repositoryId,
                CancellationToken.None);

            if (depInfo == null || !WorkspaceDependencyService.ShouldShowSynchronizedPushModal(depInfo, repoIdsThatNeedPush))
            {
                await PushSingleRepositoryWithUpstreamAsync(repositoryId, branchName);
                return;
            }

            var repoName = workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId)?.Repository?.RepositoryName;
            _pushWithDependenciesModal = _pushWithDependenciesModal with
            {
                IsVisible = true,
                Info = depInfo,
                RepoIdsToPush = null,
                RepoId = repositoryId,
                BranchName = branchName,
                RepoName = repoName
            };
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting push dependency info for repository {RepositoryId}", repositoryId);
            ToastService.Show("Could not load dependency info. Try again.");
        }
    }

    private void ClosePushWithDependenciesModal()
    {
        _pushWithDependenciesModal = _pushWithDependenciesModal with { IsVisible = false, Info = null, RepoIdsToPush = null };
    }

    /// <summary>Proceed from PushWithDependencies modal: synchronized push = sync registries then level-order push with wait; otherwise push all repos in parallel (MaxParallelOperations).</summary>
    private async Task OnPushWithDependenciesProceedAsync(bool synchronizedPush)
    {
        if (_pushWithDependenciesModal.Info == null || workspace == null)
            return;

        var repoIds = _pushWithDependenciesModal.RepoIdsToPush != null
            ? _pushWithDependenciesModal.RepoIdsToPush
            : _pushWithDependenciesModal.Info.DependencyRepoIds.Concat(new[] { _pushWithDependenciesModal.RepoId }).ToHashSet();
        var requiredPackageIds = _pushWithDependenciesModal.Info.PayloadForRepo.RequiredPackages
            .Select(r => r.PackageId?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ClosePushWithDependenciesModal();

        _pushCts?.Cancel();
        _pushCts?.Dispose();
        _pushCts = new CancellationTokenSource();
        isPushing = true;

        try
        {
            await WorkspacePushHandler.RunPushWithDependenciesAsync(
                WorkspaceId,
                repoIds,
                synchronizedPush,
                requiredPackageIds,
                SetPushProgress,
                RefreshFromSync,
                ToastService.Show,
                onAppSideComplete: () => _pushAwaitingAgentTasks = true,
                _pushCts.Token);
        }
        catch (OperationCanceledException)
        {
            ToastService.Show("Push cancelled.");
        }
        finally
        {
            _pushAwaitingAgentTasks = false;
            isPushing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Push with upstream for a single repository (e.g. when user clicks the not-upstreamed badge). Uses the page overlay.</summary>
    private async Task PushSingleRepositoryWithUpstreamAsync(int repositoryId, string? branchName)
    {
        if (workspace == null || isPushing || isSyncing || isUpdating)
            return;

        _pushCts?.Cancel();
        _pushCts?.Dispose();
        _pushCts = new CancellationTokenSource();
        isPushing = true;
        SetPushProgress("Setting upstream...");

        try
        {
            var (success, errorMessage) = await WorkspacePushHandler.PushSingleRepositoryWithUpstreamAsync(
                WorkspaceId,
                repositoryId,
                branchName,
                SetPushProgress,
                _pushCts.Token);

            if (success)
                await RefreshFromSync();
            else
                ToastService.Show(errorMessage ?? "Push failed.");
        }
        catch (OperationCanceledException)
        {
            ToastService.Show("Push cancelled.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Push with upstream failed for repository {RepositoryId}", repositoryId);
            ToastService.Show(ex.Message);
        }
        finally
        {
            isPushing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Push button click: get push plan; filter to repos with unpushed commits or no upstream branch; show modal only if at least one dependency repo needs push; otherwise push immediately.</summary>
    private async Task OnPushClickAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || isPushing || isSyncing || isUpdating)
            return;

        try
        {
            var (payload, hasUnpushed) = await WorkspacePushHandler.GetPushPlanAsync(
                WorkspaceId,
                workspaceRepositories,
                CancellationToken.None);
            if (!hasUnpushed)
            {
                ToastService.Show("No repositories to push.");
                return;
            }

            var repoIdsWithUnpushed = payload.Select(p => p.RepoId).ToHashSet();
            pushPlanRepoIds = repoIdsWithUnpushed;

            var repoIdsThatNeedPush = workspaceRepositories
                .Where(wr => (wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false)
                .Select(wr => wr.RepositoryId)
                .ToHashSet();
            var depInfo = await WorkspaceDependencyService.GetPushDependencyInfoForRepoSetAsync(
                WorkspaceId,
                repoIdsWithUnpushed,
                CancellationToken.None);
            if (depInfo == null)
            {
                ToastService.Show("Could not load push plan. Try again.");
                return;
            }
            _pushWithDependenciesModal = _pushWithDependenciesModal with
            {
                Info = depInfo,
                RepoIdsToPush = repoIdsWithUnpushed,
                RepoId = 0,
                RepoName = null
            };

            // Push immediately without dialog when there are no deps, or when no dependency repo needs push.
            if ((depInfo.PayloadForRepo.RequiredPackages.Count == 0 && depInfo.DependencyRepoIds.Count == 0)
                || !WorkspaceDependencyService.ShouldShowSynchronizedPushModal(depInfo, repoIdsThatNeedPush))
            {
                await OnPushWithDependenciesProceedAsync(synchronizedPush: false);
                return;
            }

            _pushWithDependenciesModal = _pushWithDependenciesModal with { IsVisible = true };
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting push plan for workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Could not determine push plan. The GrayMoon Agent may be offline.";
        }
    }

    /// <summary>Pull button click (when any repo has incoming commits): run commit sync (Pull) only for repos with incoming commits. Uses same overlay and merge/error handling as CommitSyncAsync/CommitSyncLevelAsync.</summary>
    private void OnPullClickAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || isCommitSyncing || isSyncing || isUpdating)
            return;

        var repoIdsWithIncoming = workspaceRepositories
            .Where(wr => (wr.IncomingCommits ?? 0) > 0)
            .Select(wr => wr.RepositoryId)
            .ToList();
        if (repoIdsWithIncoming.Count == 0)
        {
            ToastService.Show("No repositories with incoming commits to pull.");
            return;
        }
        if (repoIdsWithIncoming.Count == 1)
            ShowConfirm("Do you want to pull for this repository?", () => CommitSyncAsync(repoIdsWithIncoming[0]));
        else
            ShowConfirm($"Do you want to pull for {repoIdsWithIncoming.Count} repositories?", () => CommitSyncLevelAsync(repoIdsWithIncoming));
    }

    /// <summary>Update button click: get update plan; if no updates, toast; else show modal (single vs multi-level).</summary>
    private async Task OnUpdateClickAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || isUpdating || isSyncing)
            return;

        try
        {
            _updateModal = _updateModal with
            {
                IsVisible = true
            };
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting update plan for workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Could not determine update plan. The GrayMoon Agent may be offline.";
        }
    }

    private async Task OnUpdateProceedAsync()
    {
        CloseUpdateModal();
        await RunUpdateCoreAsync();
    }

    /// <summary>Runs update (refresh, sync deps, commit per level, refresh version). Overlay shows progress.</summary>
    private async Task RunUpdateCoreAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || isUpdating || isSyncing)
            return;

        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource();

        isUpdating = true;
        SetUpdateProgress("Updating dependencies...");
        errorMessage = null;
        try
        {
            StateHasChanged();
            await Task.Yield();

            await WorkspaceUpdateHandler.RunUpdateAsync(
                WorkspaceId,
                _updateCts.Token,
                SetUpdateProgress,
                (repoId, msg) =>
                {
                    repositoryErrors[repoId] = msg;
                    _ = InvokeAsync(StateHasChanged);
                },
                onAppSideComplete: () => _updateAwaitingAgentTasks = true,
                repoIdsToUpdate: null,
                onVersionFilesUpdated: HandleVersionFilesUpdated);

            isUpdating = false;
            await InvokeAsync(StateHasChanged);
            await RefreshFromSync();
        }
        catch (OperationCanceledException)
        {
            isUpdating = false;
            await ReloadWorkspaceDataAfterCancelAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating dependencies for workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Update failed. The GrayMoon Agent may be offline. Start the Agent and try again.";
        }
        finally
        {
            _updateAwaitingAgentTasks = false;
            isUpdating = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Commit file-version updates (no dependency updates path). Stages and commits the given paths per repo via agent StageAndCommit.</summary>
    private async Task CommitFileVersionUpdatesAsync(IReadOnlyList<(int RepoId, string RepoName, IReadOnlyList<string> FilePaths)> byRepo)
    {
        if (byRepo == null || byRepo.Count == 0)
            return;

        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource();
        isUpdating = true;
        SetUpdateProgress("Committing updated versions...");
        try
        {
            await InvokeAsync(StateHasChanged);
            await using (var scope = ServiceScopeFactory.CreateAsyncScope())
            {
                var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                var commitResults = await workspaceGitService.CommitFilePathsAsync(
                    WorkspaceId,
                    byRepo,
                    onProgress: OnCommitDependencyProgress,
                    cancellationToken: _updateCts.Token);
                foreach (var (repoId, errMsg) in commitResults)
                {
                    if (!string.IsNullOrEmpty(errMsg))
                        repositoryErrors[repoId] = errMsg;
                }
            }
            await RefreshFromSync();
        }
        finally
        {
            isUpdating = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Opens the dedicated version-files commit modal with message and file list.</summary>
    private void OpenVersionFilesCommitModal(
        string message,
        IReadOnlyList<(int RepoId, string RepoName, IReadOnlyList<string> FilePaths)> byRepo,
        IReadOnlyList<string> filesForDisplay)
    {
        _versionFilesCommitModal = _versionFilesCommitModal with
        {
            IsVisible = true,
            Message = message,
            ByRepoToCommit = byRepo,
            Files = filesForDisplay
        };
        StateHasChanged();
    }

    private void CloseVersionFilesCommitModal()
    {
        _versionFilesCommitModal = _versionFilesCommitModal with
        {
            IsVisible = false,
            ByRepoToCommit = null,
            Files = Array.Empty<string>()
        };
        StateHasChanged();
    }

    private async Task OnVersionFilesCommitProceedAsync()
    {
        var byRepo = _versionFilesCommitModal.ByRepoToCommit;
        CloseVersionFilesCommitModal();
        if (byRepo == null || byRepo.Count == 0)
            return;
        await CommitFileVersionUpdatesAsync(byRepo);
    }

    private async Task HandleDependencyBadgeKeydown(KeyboardEventArgs e, int repositoryId, int unmatchedDeps)
    {
        if ((e.Key == "Enter" || e.Key == " ") && !isUpdating && !isSyncing)
            await ShowConfirmUpdateDependenciesAsync(repositoryId, unmatchedDeps);
    }

    /// <summary>Update dependencies for a single repository only (refresh projects, sync deps, no commit). Same as Update but scoped to one repo.</summary>
    private async Task UpdateSingleRepositoryAsync(int repositoryId)
    {
        if (workspace == null || isUpdating || isSyncing)
            return;

        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource();

        isUpdating = true;
        SetUpdateProgress("Updating repository...");
        errorMessage = null;
        repositoryErrors.Remove(repositoryId);
        try
        {
            StateHasChanged();
            await Task.Yield();

            await using (var scope = ServiceScopeFactory.CreateAsyncScope())
            {
                var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                await workspaceGitService.RunUpdateSingleRepositoryAsync(
                    WorkspaceId,
                    repositoryId,
                    onProgressMessage: SetUpdateProgress,
                    onRepoError: (repoId, msg) =>
                    {
                        repositoryErrors[repoId] = msg;
                        InvokeAsync(StateHasChanged);
                    },
                    cancellationToken: _updateCts.Token);
            }

            isUpdating = false;
            await InvokeAsync(StateHasChanged);
            await RefreshFromSync();
        }
        catch (OperationCanceledException)
        {
            isUpdating = false;
            await ReloadWorkspaceDataAfterCancelAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating repository {RepositoryId} in workspace {WorkspaceId}", repositoryId, WorkspaceId);
            repositoryErrors[repositoryId] = "Update failed. The GrayMoon Agent may be offline. Start the Agent and try again.";
        }
        finally
        {
            isUpdating = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ShowRepositoriesModalAsync()
    {
        if (workspace == null)
        {
            return;
        }
        _repositoriesModal.ErrorMessage = null;
        _repositoriesModal.SelectedRepositoryIds = workspace.Repositories
            .Select(link => link.RepositoryId)
            .ToHashSet();
        await EnsureRepositoriesForModalAsync();
        _repositoriesModal.IsVisible = true;
    }

    private async Task EnsureRepositoriesForModalAsync()
    {
        var connectors = await WorkspacePageService.ConnectorRepository.GetAllAsync();
        _repositoriesModal.HasConnectors = connectors.Count > 0;
        if (_repositoriesModal.Repositories == null && _repositoriesModal.HasConnectors)
        {
            _repositoriesModal.Repositories = await WorkspacePageService.RepositoryService.GetPersistedRepositoriesAsync();
        }
    }

    private void CloseRepositoriesModal()
    {
        _repositoriesModal.IsVisible = false;
        _repositoriesModal.ErrorMessage = null;
    }

    private async Task SaveRepositoriesAsync()
    {
        if (workspace == null || _repositoriesModal.IsSaving)
        {
            return;
        }
        if (_repositoriesModal.SelectedRepositoryIds.Count == 0)
        {
            _repositoriesModal.ErrorMessage = "Select at least one repository.";
            return;
        }
        _repositoriesModal.IsSaving = true;
        _repositoriesModal.ErrorMessage = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            await WorkspacePageService.WorkspaceRepository.UpdateAsync(WorkspaceId, workspace.Name, _repositoriesModal.SelectedRepositoryIds);
            CloseRepositoriesModal();
            await ReloadWorkspaceDataAsync();
            ApplySyncStateFromWorkspace();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error saving repositories for workspace {WorkspaceId}", WorkspaceId);
            _repositoriesModal.ErrorMessage = ex.Message;
        }
        finally
        {
            _repositoriesModal.IsSaving = false;
        }
    }

    private void AbortFetchRepositories()
    {
        _fetchRepositoriesCts?.Cancel();
    }

    private async Task FetchRepositoriesAsync()
    {
        if (!_repositoriesModal.HasConnectors || _repositoriesModal.IsFetching)
        {
            return;
        }
        _fetchRepositoriesCts?.Cancel();
        _fetchRepositoriesCts = new CancellationTokenSource();
        try
        {
            _repositoriesModal.IsFetching = true;
            _repositoriesModal.FetchedRepositoryCount = null;
            _repositoriesModal.ErrorMessage = null;
            await InvokeAsync(StateHasChanged);
            var progress = new Progress<int>(count =>
            {
                _repositoriesModal.FetchedRepositoryCount = count;
                _ = InvokeAsync(StateHasChanged);
            });
            var result = await WorkspacePageService.RepositoryService.RefreshRepositoriesAsync(progress, _fetchRepositoriesCts.Token);
            _repositoriesModal.Repositories = result.Repositories.ToList();
        }
        catch (OperationCanceledException)
        {
            _repositoriesModal.Repositories = await WorkspacePageService.RepositoryService.GetPersistedRepositoriesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error fetching repositories");
            _repositoriesModal.ErrorMessage = "Failed to fetch repositories. Please try again later.";
            _repositoriesModal.Repositories = new List<GitHubRepositoryEntry>();
        }
        finally
        {
            _repositoriesModal.IsFetching = false;
        }
    }

    /// <summary>Sync repos only (git, version, branch, commit counts). Does not read or write .csproj; dependency mismatches are resolved only by Update. Runs in a fresh scope so DbContext is not stale and PersistVersionsAsync â†’ dependency recompute sees current state.</summary>
    private async Task SyncAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || isSyncing)
        {
            return;
        }

        _syncCts?.Cancel();
        _syncCts = new CancellationTokenSource();

        isSyncing = true;
        SetSyncProgress("Synchronizing...");
        var isRetryAfterError = !string.IsNullOrEmpty(errorMessage);
        errorMessage = null;
        try
        {
            StateHasChanged();
            await Task.Yield();

            repoGitInfos = await WorkspaceSyncHandler.RunSyncAsync(
                WorkspaceId,
                repositoryIds: null,
                skipDependencyLevelPersistence: isRetryAfterError,
                cancellationToken: _syncCts.Token,
                setProgress: SetSyncProgress,
                updateRepoGitInfo: (repoId, info) =>
                {
                    var wr = workspaceRepositories.FirstOrDefault(w => w.RepositoryId == repoId);
                    if (wr != null)
                    {
                        wr.GitVersion = info.Version == "-" ? null : info.Version;
                        wr.BranchName = info.Branch == "-" ? null : info.Branch;
                        wr.Projects = info.Projects;
                        wr.OutgoingCommits = info.OutgoingCommits;
                        wr.IncomingCommits = info.IncomingCommits;
                    }
                    _ = InvokeAsync(StateHasChanged);
                },
                setRepoSyncStatus: (repoId, status) => repoSyncStatus[repoId] = status,
                onAppSideComplete: () => _syncAwaitingAgentTasks = true);

            await ReloadWorkspaceDataAsync();
            ApplySyncStateFromWorkspace();
            isOutOfSync = repoSyncStatus.Values.Any(v => v != RepoSyncStatus.InSync);

            foreach (var (repoId, info) in repoGitInfos)
            {
                if (!string.IsNullOrWhiteSpace(info.ErrorMessage))
                    repositoryErrors[repoId] = info.ErrorMessage;
                else
                    repositoryErrors.Remove(repoId);
            }
        }
        catch (OperationCanceledException)
        {
            await ReloadWorkspaceDataAfterCancelAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error syncing workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Sync failed. The GrayMoon Agent may be offline. Start the Agent and try again.";
        }
        finally
        {
            _syncAwaitingAgentTasks = false;
            isSyncing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Sync (refresh version, branch, commits) for the given repositories in a level. Uses skipDependencyLevelPersistence so merge does not persist with partial edges; PersistVersionsAsync then recomputes levels from full DB. Same overlay and error handling as SyncAsync.</summary>
    private async Task SyncLevelAsync(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0 || isSyncing)
            return;

        _syncCts?.Cancel();
        _syncCts = new CancellationTokenSource();

        isSyncing = true;
        SetSyncProgress($"Synchronizing {repositoryIds.Count} {(repositoryIds.Count == 1 ? "repository" : "repositories")}...");
        errorMessage = null;
        try
        {
            StateHasChanged();
            await Task.Yield();

            repoGitInfos = await WorkspaceSyncHandler.RunSyncAsync(
                WorkspaceId,
                repositoryIds,
                skipDependencyLevelPersistence: true,
                cancellationToken: _syncCts.Token,
                setProgress: SetSyncProgress,
                updateRepoGitInfo: (repoId, info) =>
                {
                    var wr = workspaceRepositories.FirstOrDefault(w => w.RepositoryId == repoId);
                    if (wr != null)
                    {
                        wr.GitVersion = info.Version == "-" ? null : info.Version;
                        wr.BranchName = info.Branch == "-" ? null : info.Branch;
                        wr.Projects = info.Projects;
                        wr.OutgoingCommits = info.OutgoingCommits;
                        wr.IncomingCommits = info.IncomingCommits;
                    }
                    _ = InvokeAsync(StateHasChanged);
                },
                setRepoSyncStatus: (repoId, status) => repoSyncStatus[repoId] = status,
                onAppSideComplete: () => _syncAwaitingAgentTasks = true);

            await ReloadWorkspaceDataAsync();
            ApplySyncStateFromWorkspace();
            isOutOfSync = repoSyncStatus.Values.Any(v => v != RepoSyncStatus.InSync);

            foreach (var (repoId, info) in repoGitInfos)
            {
                if (!string.IsNullOrWhiteSpace(info.ErrorMessage))
                    repositoryErrors[repoId] = info.ErrorMessage;
                else
                    repositoryErrors.Remove(repoId);
            }
        }
        catch (OperationCanceledException)
        {
            await ReloadWorkspaceDataAfterCancelAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error syncing level for workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Sync failed. The GrayMoon Agent may be offline. Start the Agent and try again.";
        }
        finally
        {
            _syncAwaitingAgentTasks = false;
            isSyncing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Sync a single repository (same as Sync button but for one repo). Levels are recomputed after persist via full DB graph (see PersistVersionsAsync when skipDependencyLevelPersistence).</summary>
    private async Task SyncSingleRepoAsync(int repositoryId)
    {
        if (workspace == null || workspaceRepositories.Count == 0 || isSyncing)
            return;

        _syncCts?.Cancel();
        _syncCts = new CancellationTokenSource();

        isSyncing = true;
        SetSyncProgress("Synchronizing repository...");
        errorMessage = null;
        try
        {
            StateHasChanged();
            await Task.Yield();

            repoGitInfos = await WorkspaceSyncHandler.RunSyncAsync(
                WorkspaceId,
                new[] { repositoryId },
                skipDependencyLevelPersistence: true,
                cancellationToken: _syncCts.Token,
                setProgress: SetSyncProgress,
                updateRepoGitInfo: (repoId, info) =>
                {
                    var wr = workspaceRepositories.FirstOrDefault(w => w.RepositoryId == repoId);
                    if (wr != null)
                    {
                        wr.GitVersion = info.Version == "-" ? null : info.Version;
                        wr.BranchName = info.Branch == "-" ? null : info.Branch;
                        wr.Projects = info.Projects;
                        wr.OutgoingCommits = info.OutgoingCommits;
                        wr.IncomingCommits = info.IncomingCommits;
                    }
                    _ = InvokeAsync(StateHasChanged);
                },
                setRepoSyncStatus: (repoId, status) => repoSyncStatus[repoId] = status,
                onAppSideComplete: () => _syncAwaitingAgentTasks = true);
            await ReloadWorkspaceDataAsync();
            ApplySyncStateFromWorkspace();
            isOutOfSync = repoSyncStatus.Values.Any(v => v != RepoSyncStatus.InSync);

            foreach (var (repoId, info) in repoGitInfos)
            {
                if (!string.IsNullOrWhiteSpace(info.ErrorMessage))
                    repositoryErrors[repoId] = info.ErrorMessage;
                else
                    repositoryErrors.Remove(repoId);
            }
        }
        catch (OperationCanceledException)
        {
            await ReloadWorkspaceDataAfterCancelAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error syncing repository {RepositoryId} in workspace {WorkspaceId}", repositoryId, WorkspaceId);
            errorMessage = "Sync failed. The GrayMoon Agent may be offline. Start the Agent and try again.";
        }
        finally
        {
            _syncAwaitingAgentTasks = false;
            isSyncing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task CommitSyncAsync(int repositoryId)
    {
        if (workspace == null || isCommitSyncing || isSyncing || isUpdating)
            return;

        _commitSyncCts?.Cancel();
        _commitSyncCts?.Dispose();
        _commitSyncCts = new CancellationTokenSource();

        isCommitSyncing = true;
        SetCommitSyncProgress("Synchronizing commits...");
        errorMessage = null;
        try
        {
            StateHasChanged();
            await Task.Yield();

            await WorkspaceCommitSyncHandler.CommitSyncAsync(
                WorkspaceId,
                repositoryId,
                ApiBaseUrl,
                _commitSyncCts.Token,
                async message =>
                {
                    SetCommitSyncProgress(message);
                    await Task.CompletedTask;
                },
                (id, err) =>
                {
                    if (err is null)
                        repositoryErrors.Remove(id);
                    else
                        repositoryErrors[id] = err;
                },
                msg => errorMessage = msg);

            await RefreshFromSync();
        }
        catch (OperationCanceledException)
        {
            await ReloadWorkspaceDataAfterCancelAsync();
        }
        finally
        {
            isCommitSyncing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task CommitSyncLevelAsync(List<int> repositoryIds)
    {
        if (workspace == null || isCommitSyncing || isSyncing || isUpdating || repositoryIds == null || repositoryIds.Count == 0)
            return;

        _commitSyncCts?.Cancel();
        _commitSyncCts?.Dispose();
        _commitSyncCts = new CancellationTokenSource();

        isCommitSyncing = true;
        SetCommitSyncProgress("Synchronizing commits...");
        errorMessage = null;
        try
        {
            StateHasChanged();
            await Task.Yield();

            await WorkspaceCommitSyncHandler.CommitSyncLevelAsync(
                WorkspaceId,
                repositoryIds,
                ApiBaseUrl,
                _commitSyncCts.Token,
                async (completed, total) =>
                {
                    SetCommitSyncProgress($"Synchronized commits {completed} of {total}");
                    if (completed == total)
                        _commitSyncAwaitingAgentTasks = true;
                    await InvokeAsync(StateHasChanged);
                },
                (id, err) =>
                {
                    if (err is null)
                        repositoryErrors.Remove(id);
                    else
                        repositoryErrors[id] = err;
                },
                msg => errorMessage = msg);

            await RefreshFromSync();
        }
        catch (OperationCanceledException)
        {
            await ReloadWorkspaceDataAfterCancelAsync();
        }
        finally
        {
            _commitSyncAwaitingAgentTasks = false;
            isCommitSyncing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void ShowSwitchBranchModal(int repositoryId, string? currentBranch, string? cloneUrl)
    {
        var wr = workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId);
        var repo = wr?.Repository;

        if (repo == null)
            return;

        _switchBranchModal = _switchBranchModal with
        {
            IsVisible = true,
            RepositoryId = repositoryId,
            RepositoryName = repo.RepositoryName,
            CurrentBranch = currentBranch,
            RepositoryUrl = cloneUrl ?? repo.CloneUrl
        };
        StateHasChanged();
    }

    private void CloseSwitchBranchModal()
    {
        _switchBranchModal = _switchBranchModal with
        {
            IsVisible = false,
            RepositoryId = 0,
            RepositoryName = null,
            CurrentBranch = null,
            RepositoryUrl = null
        };
    }

    private async Task ShowBranchModalAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0)
            return;
        try
        {
            var data = await WorkspaceBranchHandler.GetCommonBranchesAsync(
                WorkspaceId,
                ApiBaseUrl,
                CancellationToken.None);
            if (data != null)
            {
                _branchModal = _branchModal with
                {
                    CommonBranchNames = data.CommonBranchNames ?? new List<string>(),
                    DefaultDisplayText = data.DefaultDisplayText ?? "multiple"
                };
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load common branches for branch modal");
        }
        _branchModal = _branchModal with { IsVisible = true };
        StateHasChanged();
    }

    private void CloseBranchModal()
    {
        _branchModal = _branchModal with { IsVisible = false };
    }

    private async Task CreateBranchesAsync((string NewBranchName, string BaseBranch) args)
    {
        var (newBranchName, baseBranch) = args;
        if (workspace == null || string.IsNullOrWhiteSpace(newBranchName) || isCreatingBranches || isSyncing || isUpdating)
            return;

        CloseBranchModal();
        _createBranchesCts?.Cancel();
        _createBranchesCts?.Dispose();
        _createBranchesCts = new CancellationTokenSource();

        isCreatingBranches = true;
        SetCreateBranchesProgress("Creating branches...");
        errorMessage = null;
        StateHasChanged();

        try
        {
            await Task.Yield();
            await WorkspaceBranchHandler.CreateBranchesAsync(
                WorkspaceId,
                newBranchName,
                baseBranch,
                (completed, total) =>
                {
                    SetCreateBranchesProgress($"Created {completed} of {total} branches");
                    if (completed == total)
                        _creatingBranchesAwaitingAgentTasks = true;
                    _ = InvokeAsync(StateHasChanged);
                },
                _createBranchesCts.Token);
            await RefreshFromSync();
        }
        catch (OperationCanceledException)
        {
            await ReloadWorkspaceDataAfterCancelAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error creating branches for workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Create branches failed. The GrayMoon Agent may be offline. Start the Agent and try again.";
        }
        finally
        {
            _creatingBranchesAwaitingAgentTasks = false;
            isCreatingBranches = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task OnBranchChangedAsync()
    {
        // Refresh workspace data to show updated branch
        await RefreshFromSync();
    }

    private async Task CreateSingleBranchAsync((int RepositoryId, string? RepositoryName, string NewBranchName, string BaseBranch, bool SetUpstream) request)
    {
        var (repositoryId, repositoryName, newBranchName, baseBranch, setUpstream) = request;
        if (workspace == null || isCreatingBranch || isSyncing || isUpdating)
            return;

        CloseSwitchBranchModal();
        isCreatingBranch = true;
        createBranchMessage = "Creating branch...";
        errorMessage = null;
        await InvokeAsync(StateHasChanged);

        try
        {
            var (success, err) = await WorkspaceBranchHandler.CreateSingleBranchAsync(
                WorkspaceId,
                repositoryId,
                newBranchName,
                baseBranch,
                setUpstream,
                ApiBaseUrl,
                CancellationToken.None);
            if (!success)
            {
                errorMessage = err ?? "Create branch failed. The GrayMoon Agent may be offline. Start the Agent and try again.";
            }
            else
            {
                if (err != null)
                    errorMessage = err;
                await RefreshFromSync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error creating branch for repository {RepositoryId}", repositoryId);
            errorMessage = "An error occurred while creating branch.";
        }
        finally
        {
            isCreatingBranch = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task SyncToDefaultFromModalAsync((int RepositoryId, string? RepositoryName, string CurrentBranchName, string DefaultBranch) request)
    {
        var (repositoryId, repositoryName, currentBranchName, defaultBranch) = request;
        if (workspace == null || isSyncingToDefault || isSyncing || isUpdating)
            return;
        if (string.IsNullOrWhiteSpace(repositoryName))
            return;

        CloseSwitchBranchModal();

        try
        {
            // Use persisted workspace link state (updated by hooks); no agent GetCommitCounts call.
            var wr = workspaceRepositories.FirstOrDefault(w => w.RepositoryId == repositoryId);
            var defaultAhead = wr?.DefaultBranchAheadCommits ?? 0;
            var hasUpstream = wr?.BranchHasUpstream == true;

            if (defaultAhead > 0 && !IsPrMergedForRepo(repositoryId))
            {
                ToastService.Show("Skipped sync to default: commits ahead of default branch and PR is not merged.");
                return;
            }
            await SyncToDefaultSingleRepoAfterCheckAsync(repositoryId, repositoryName, currentBranchName, hasUpstream, defaultBranch);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error preparing sync to default (repository {RepositoryId})", repositoryId);
            ToastService.Show("Failed to prepare sync to default.");
        }
    }

    private async Task SyncToDefaultSingleRepoAfterCheckAsync(int repositoryId, string repositoryName, string currentBranchName, bool deleteRemoteFirst, string? defaultBranchName = null)
    {
        if (workspace == null || isSyncingToDefault || isSyncing || isUpdating)
            return;

        isSyncingToDefault = true;
        syncToDefaultMessage = string.IsNullOrWhiteSpace(defaultBranchName) ? "Synchronizing to default branch..." : $"Synchronizing to {defaultBranchName}...";
        errorMessage = null;
        await InvokeAsync(StateHasChanged);

        try
        {
            var (success, errMsg) = await WorkspaceBranchHandler.SyncToDefaultSingleAsync(
                WorkspaceId,
                repositoryId,
                currentBranchName,
                deleteRemoteFirst,
                ApiBaseUrl,
                CancellationToken.None);

            if (success)
            {
                repositoryErrors.Remove(repositoryId);
                await RefreshFromSync();
            }
            else if (errMsg != null)
            {
                repositoryErrors[repositoryId] = errMsg;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error syncing to default branch for repository {RepositoryId}", repositoryId);
            repositoryErrors[repositoryId] = "An error occurred while syncing to default branch. The GrayMoon Agent may be offline.";
        }
        finally
        {
            _syncToDefaultAwaitingAgentTasks = false;
            isSyncingToDefault = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task SyncToDefaultLevelAsync(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0 || isSyncingToDefault || isSyncing || isUpdating)
            return;

        var checkResults = _syncToDefaultCheckResults;
        _syncToDefaultCheckResults = null;

        isSyncingToDefault = true;
        var total = repositoryIds.Count;
        syncToDefaultMessage = total > 1 ? "Synchronizing to default branch..." : "Synchronizing to default branch...";
        errorMessage = null;
        await InvokeAsync(StateHasChanged);

        var maxParallel = Math.Max(1, WorkspaceOptions?.Value?.MaxParallelOperations ?? 16);
        var resultByRepo = checkResults?.ToDictionary(r => r.RepoId) ?? new Dictionary<int, (int RepoId, int? DefaultAhead, bool? HasUpstream)>();
        var completedCount = 0;

        try
        {
            using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);

            var tasks = repositoryIds.Select(async repositoryId =>
            {
                var wr = workspaceRepositories.FirstOrDefault(w => w.RepositoryId == repositoryId);
                var currentBranchName = wr?.BranchName;
                if (string.IsNullOrWhiteSpace(currentBranchName))
                {
                    var c = Interlocked.Increment(ref completedCount);
                    if (total > 1)
                    {
                        syncToDefaultMessage = $"Synchronized {c} of {total} to default branch";
                        if (c == total)
                            _syncToDefaultAwaitingAgentTasks = true;
                        await InvokeAsync(StateHasChanged);
                    }
                    return (repositoryId, true, (string?)null);
                }

                await semaphore.WaitAsync();
                try
                {
                    var hasUpstream = resultByRepo.TryGetValue(repositoryId, out var r) && r.HasUpstream == true;

                    var (success, errMsg) = await WorkspaceBranchHandler.SyncToDefaultSingleAsync(
                        WorkspaceId,
                        repositoryId,
                        currentBranchName,
                        hasUpstream,
                        ApiBaseUrl,
                        CancellationToken.None);

                    return (repositoryId, success, errMsg);
                }
                finally
                {
                    semaphore.Release();
                    var c = Interlocked.Increment(ref completedCount);
                    if (total > 1)
                    {
                        syncToDefaultMessage = $"Synchronized {c} of {total} to default branch";
                        if (c == total)
                            _syncToDefaultAwaitingAgentTasks = true;
                        await InvokeAsync(StateHasChanged);
                    }
                }
            });

            var results = await Task.WhenAll(tasks);
            var successCount = results.Count(r => r.Item2);
            foreach (var (repoId, success, errMsg) in results)
            {
                if (success)
                {
                    repositoryErrors.Remove(repoId);
                }
                else if (errMsg != null)
                {
                    repositoryErrors[repoId] = errMsg;
                    var repoName = workspaceRepositories.FirstOrDefault(w => w.RepositoryId == repoId)?.Repository?.RepositoryName ?? repoId.ToString();
                    ToastService.Show($"{repoName}: {errMsg}");
                }
            }
            if (total > 1)
            {
                syncToDefaultMessage = $"Synchronized {successCount} of {total} to default branch";
                if (successCount == total)
                    _syncToDefaultAwaitingAgentTasks = true;
            }
            await InvokeAsync(StateHasChanged);
            await RefreshFromSync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error syncing to default branch for level");
            errorMessage = "An error occurred while syncing to default branch. The GrayMoon Agent may be offline.";
        }
        finally
        {
            _syncToDefaultAwaitingAgentTasks = false;
            isSyncingToDefault = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task CheckoutBranchAsync((int RepositoryId, string BranchName) request)
    {
        var (repositoryId, branchName) = request;
        if (workspace == null || isCheckingOut || isSyncing || isUpdating || isCommitSyncing)
            return;

        _checkoutCts?.Cancel();
        _checkoutCts?.Dispose();
        _checkoutCts = new CancellationTokenSource();

        isCheckingOut = true;

        SetCheckoutProgress("Checking out branch...");

        errorMessage = null;
        try
        {
            StateHasChanged();
            await Task.Yield();

            var (success, errMsg) = await WorkspaceBranchHandler.CheckoutBranchAsync(
                WorkspaceId,
                repositoryId,
                branchName,
                ApiBaseUrl,
                _checkoutCts.Token);

            if (success)
            {
                repositoryErrors.Remove(repositoryId);
            }
            else
            {
                repositoryErrors[repositoryId] = errMsg ?? "Failed to checkout branch.";
            }

            await RefreshFromSync();
        }
        catch (OperationCanceledException)
        {
            await ReloadWorkspaceDataAfterCancelAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error checking out branch for repository {RepositoryId}", repositoryId);
            repositoryErrors[repositoryId] = "Failed to checkout branch. The GrayMoon Agent may be offline. Start the Agent and try again.";
        }
        finally
        {
            isCheckingOut = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void AbortCheckoutAsync()
    {
        _checkoutCts?.Cancel();
    }

    private void DismissRepositoryError(int repositoryId)
    {
        repositoryErrors.Remove(repositoryId);
        StateHasChanged();
    }

    private async Task CopyVersionToClipboard(string version)
    {
        if (!string.IsNullOrEmpty(version))
        {
            await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", version);
            ToastService.Show($"{version} copied to the clipboard");

            // Hide highlight after click (like a button)
            clickedVersions.Add(version);
            StateHasChanged();
        }
    }

    private void OnVersionMouseLeave(string? version)
    {
        if (!string.IsNullOrEmpty(version))
        {
            // Clear clicked state when mouse leaves, allowing hover to work again
            clickedVersions.Remove(version);
            StateHasChanged();
        }
    }

    private void OnDependencyBadgeClick(int repositoryId, int unmatchedDeps)
    {
        clickedDependencyBadges.Add(repositoryId);
        _ = ShowConfirmUpdateDependenciesAsync(repositoryId, unmatchedDeps);
        StateHasChanged();
    }

    private void OnDependencyBadgeMouseLeave(int repositoryId)
    {
        if (clickedDependencyBadges.Remove(repositoryId))
        {
            StateHasChanged();
        }
    }

    private void OnSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            searchTerm = string.Empty;
            StateHasChanged();
        }
    }

    private void ClearSearchFilter()
    {
        searchTerm = string.Empty;
        StateHasChanged();
    }

    private const int TableColSpan = 4;

    private PullRequestInfo? GetPrInfoForRepository(int repositoryId)
    {
        return prByRepositoryId.TryGetValue(repositoryId, out var pr) ? pr : null;
    }

    private string? GetRepositoryError(int repositoryId)
    {
        return repositoryErrors.TryGetValue(repositoryId, out var err) ? err : null;
    }

    private RepoSyncStatus? GetRepoSyncStatus(int repositoryId)
    {
        return repoSyncStatus.TryGetValue(repositoryId, out var status) ? status : (RepoSyncStatus?)null;
    }

    private IReadOnlyList<(string PackageId, string CurrentVersion, string NewVersion)> GetMismatchedDependencyLines(int repositoryId)
    {
        return _mismatchedDependencyLinesByRepo.GetValueOrDefault(repositoryId) ?? Array.Empty<(string PackageId, string CurrentVersion, string NewVersion)>();
    }

    private List<WorkspaceRepositoryLink> GetFilteredWorkspaceRepositories()
    {
        var words = searchTerm
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Trim())
            .Where(w => w.Length > 0)
            .ToList();

        if (words.Count == 0)
            return workspaceRepositories;

        return workspaceRepositories.Where(wr =>
        {
            var repoName = wr.Repository?.RepositoryName ?? string.Empty;
            var branchName = wr.BranchName ?? string.Empty;
            var version = wr.GitVersion ?? string.Empty;
            var levelTitle = wr.DependencyLevel == null ? "No dependencies" : $"Level {wr.DependencyLevel}";
            var syncText = SyncBadgeLabels.GetSyncBadgeText(repoSyncStatus.TryGetValue(wr.RepositoryId, out var s) ? s : RepoSyncStatus.NeedsSync);
            var searchable = $"{repoName} {branchName} {version} {levelTitle} {syncText}";
            return words.All(word => searchable.Contains(word, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    private sealed record ConfirmModalState
    {
        public bool IsVisible { get; init; }
        public string Message { get; init; } = "";
        public string ButtonText { get; init; } = "Yes";
        public Func<Task>? PendingAction { get; init; }
    }

    private sealed class RepositoriesModalState
    {
        public bool IsVisible { get; set; }
        public string? ErrorMessage { get; set; }
        public List<GitHubRepositoryEntry>? Repositories { get; set; }
        public HashSet<int> SelectedRepositoryIds { get; set; } = new();
        public bool IsSaving { get; set; }
        public bool IsFetching { get; set; }
        public int? FetchedRepositoryCount { get; set; }
        public bool HasConnectors { get; set; }
    }

    private sealed record BranchModalState
    {
        public bool IsVisible { get; init; }
        public IReadOnlyList<string> CommonBranchNames { get; init; } = Array.Empty<string>();
        public string DefaultDisplayText { get; init; } = "multiple";
    }

    private sealed record SwitchBranchModalState
    {
        public bool IsVisible { get; init; }
        public int RepositoryId { get; init; }
        public string? RepositoryName { get; init; }
        public string? CurrentBranch { get; init; }
        public string? RepositoryUrl { get; init; }
    }

    private sealed record UpdateModalState
    {
        public bool IsVisible { get; init; }
    }

    private sealed record UpdateSingleRepoDependenciesModalState
    {
        public bool IsVisible { get; init; }
        public SyncDependenciesRepoPayload? Payload { get; init; }
        public int RepositoryId { get; init; }
        public string? RepoName { get; init; }
    }

    private sealed record VersionFilesCommitModalState
    {
        public bool IsVisible { get; init; }
        public string Message { get; init; } = "";
        public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
        public IReadOnlyList<(int RepoId, string RepoName, IReadOnlyList<string> FilePaths)>? ByRepoToCommit { get; init; }
    }

    private sealed record PushWithDependenciesModalState
    {
        public bool IsVisible { get; init; }
        public PushDependencyInfoForRepo? Info { get; init; }
        public IReadOnlySet<int>? RepoIdsToPush { get; init; }
        public int RepoId { get; init; }
        public string? BranchName { get; init; }
        public string? RepoName { get; init; }
    }
}

