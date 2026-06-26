using GrayMoon.Abstractions.Exceptions;
using GrayMoon.App.Components.Modals;
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
    private string? errorMessage;
    private bool isLoading = true;
    private bool? isOutOfSync = null;
    private bool hasUnmatchedDependencies => workspaceRepositories.Any(wr => !wr.IsOnTag &&
        ((wr.UnmatchedDeps ?? 0) > 0 || (wr.OutOfDateFileLines ?? 0) > 0 || (wr.OutOfDateFileRepos ?? 0) > 0));
    private bool isPushRecommended => workspaceRepositories.Any(wr => !wr.IsOnTag && ((wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false));
    private bool hasTaggedRepos => workspaceRepositories.Any(wr => wr.IsOnTag);
    /// <summary>When true, any repository on its default branch has incoming commits; header shows red Pull button and executes only Pull (commit sync) for those repos. Repos pinned to a tag are excluded.</summary>
    private bool hasIncomingCommits => workspaceRepositories.Any(wr =>
        !wr.IsOnTag
        && (wr.IncomingCommits ?? 0) > 0
        && !string.IsNullOrWhiteSpace(wr.BranchName)
        && !string.IsNullOrWhiteSpace(wr.DefaultBranchName)
        && string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal));
    private IEnumerable<IGrouping<int?, WorkspaceRepositoryLink>> LevelGroups =>
        workspaceRepositories
            .GroupBy(wr => wr.DependencyLevel)
            .OrderByDescending(g => g.Key ?? int.MinValue);

    private List<WorkspaceRepositoryLink> _filteredWorkspaceRepositories = new();
    private List<WorkspaceRepositoryLink> FilteredWorkspaceRepositories => _filteredWorkspaceRepositories;
    private void UpdateFilteredRepositories() => _filteredWorkspaceRepositories = GetFilteredWorkspaceRepositories();

    private string ApiBaseUrl => NavigationManager.BaseUri.TrimEnd('/');

    private IEnumerable<IGrouping<int?, WorkspaceRepositoryLink>> FilteredLevelGroups =>
        FilteredWorkspaceRepositories
            .GroupBy(wr => wr.DependencyLevel)
            .OrderByDescending(g => g.Key ?? int.MinValue);
    private bool HasSearchFilter => !string.IsNullOrWhiteSpace(searchTerm);
    private string RepositoriesModalTitle => $"Repositories for {workspace?.Name ?? "Workspace"}";
    private bool ShowRepositoriesFetchOverlay => _repositoriesModal.IsVisible && _repositoriesModal.IsFetching;
    private string RepositoriesFetchOverlayMessage => _repositoriesModal.FetchedRepositoryCount is null || _repositoriesModal.FetchedRepositoryCount == 0
        ? "Fetching repositories..."
        : $"Fetched {_repositoriesModal.FetchedRepositoryCount} {(_repositoriesModal.FetchedRepositoryCount == 1 ? "repository" : "repositories")}";
    private Dictionary<int, RepoSyncStatus> repoSyncStatus = new();
    private IReadOnlyDictionary<int, IReadOnlyList<(string PackageId, string CurrentVersion, string NewVersion)>> _mismatchedDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<(string, string, string)>>();
    private IReadOnlyDictionary<int, IReadOnlyList<WorkspaceFileLineStatus>> _fileLineStatusByRepo = new Dictionary<int, IReadOnlyList<WorkspaceFileLineStatus>>();

    /// <summary>All workspace-internal package dependencies of each repository (PackageId + version as written in the .csproj). Used to populate the dependency-badge tooltip for repositories whose dependencies are up to date.</summary>
    private IReadOnlyDictionary<int, IReadOnlyList<(string PackageId, string Version)>> _allDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<(string, string)>>();
    /// <summary>Per-repo (FileName, TokenName, Version) triples derived from version-file configs and current GitVersions. Drives the OK badge file-dependency tooltip.</summary>
    private IReadOnlyDictionary<int, IReadOnlyList<(string FileName, string TokenName, string Version)>> _allFileVersionLinesByRepo = new Dictionary<int, IReadOnlyList<(string, string, string)>>();
    /// <summary>User-declared custom dependency repo names per dependent repository (ordering only).</summary>
    private IReadOnlyDictionary<int, IReadOnlyList<string>> _customDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<string>>();
    private Dictionary<int, string> repositoryErrors = new(); // repositoryId -> error message
    private HashSet<string> clickedVersions = new(); // Track clicked versions to hide hover until mouse leaves
    private HashSet<int> clickedDependencyBadges = new(); // Track clicked dependency badges to hide tooltip immediately
    private HubConnection? _hubConnection;
    private CancellationTokenSource? _fetchRepositoriesCts;
    private RepositoriesModalState _repositoriesModal = new();
    private SwitchBranchModalState _switchBranchModal = new();
    private BranchModalState _branchModal = new();
    private IReadOnlyList<(int RepoId, int? DefaultAhead, bool? HasUpstream)>? _syncToDefaultCheckResults = null;
    private UpdateModalState _updateModal = new();
    private UpdateModalState _updateAndPushModal = new();
    private UpdateSingleRepoDependenciesModalState _updateSingleRepoModal = new();
    private CustomDependenciesModalState _customDependenciesModal = new();
    private PushWithDependenciesModalState _pushWithDependenciesModal = new();
    private ConfirmModalState _confirmModal = new();
    private DefaultBranchWarningModalState _defaultBranchWarningModal = new();
    private SyncToDefaultOptionsModalState _syncToDefaultOptionsModal = new();
    private UndoPushModalState _undoPushModal = new();
    private string searchTerm = string.Empty;

    private bool _disposed;
    private bool _wasJobRunning;
    private string PageJobKey => new Uri(NavigationManager.Uri).AbsolutePath.ToLowerInvariant();
    private bool IsJobRunning => JobService.IsRunning(PageJobKey);

    private int AgentTasksPendingCount => AgentQueueStateService.GetPendingCountForWorkspace(WorkspaceId);

    private const int RefreshDebounceMs = 200;
    private CancellationTokenSource? _refreshDebounceCts;
    private readonly object _refreshDebounceLock = new();
    private readonly SemaphoreSlim _loadMismatchedDepsLock = new(1, 1);

    /// <summary>Toast shown when the user attempts a write action against a repository that is pinned to a tag.</summary>
    private const string TagBlockedActionMessage = "Repository is on a tag; checkout a branch first.";

    /// <summary>True when the workspace repository link for <paramref name="repositoryId"/> is currently checked out at a tag.</summary>
    private bool IsRepoOnTag(int repositoryId) =>
        workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId)?.IsOnTag == true;

    protected override async Task OnInitializedAsync()
    {
        AgentQueueStateService.OnQueueStateChanged(OnQueueStateChanged);
        JobService.Changed += OnJobServiceChanged;
        _wasJobRunning = IsJobRunning;
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
                if (IsJobRunning) return;
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
        _hubConnection?.DisposeAsync();
        _fetchRepositoriesCts?.Cancel();
        _fetchRepositoriesCts?.Dispose();
        _loadMismatchedDepsLock.Dispose();
    }

    private void OnJobServiceChanged()
    {
        if (_disposed) return;
        _ = InvokeAsync(() =>
        {
            if (_disposed) return;
            var isRunning = IsJobRunning;
            if (_wasJobRunning && !isRunning && !_disposed)
                _ = InvokeAsync(RefreshFromSync);
            _wasJobRunning = isRunning;
            StateHasChanged();
        });
    }

    private void SafeInvoke(Action callback)
    {
        if (_disposed) return;
        _ = InvokeAsync(() => { if (!_disposed) { callback(); StateHasChanged(); } });
    }

    /// <summary>Called when WorkspaceSynced is received (or after an operation): reload from a fresh scope so the grid gets current DB values (no stale DbContext).</summary>
    private async Task RefreshFromSync()
    {
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
            UpdateFilteredRepositories();
            return;
        }

        workspaceRepositories = workspace.Repositories
            .OrderByDescending(wr => wr.DependencyLevel ?? int.MinValue)
            .ThenBy(wr => GetRepositoryTypeSortOrder(wr.RepositoryType))
            .ThenByDescending(wr => wr.Dependencies ?? int.MinValue)
            .ToList();
        prByRepositoryId = BuildPrByRepositoryIdFromLinks(workspaceRepositories);
        await LoadMismatchedDependencyLinesAsync();
        UpdateFilteredRepositories();
    }

    private static int GetRepositoryTypeSortOrder(ProjectType? type) => type switch
    {
        ProjectType.Service    => 0,
        ProjectType.Package    => 1,
        ProjectType.Executable => 2,
        ProjectType.Library    => 3,
        ProjectType.Test       => 4,
        _                      => 5 // null - no projects yet
    };

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

    private async Task RefreshBranchesForRepositoryAsync(int repositoryId)
    {
        try
        {
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var gitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            await gitService.RefreshBranchesAndBroadcastAsync(repositoryId, WorkspaceId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Branch refresh after PR merge failed for RepositoryId={RepositoryId}", repositoryId);
        }
    }

    /// <summary>Reload workspace after abort/cancel using a fresh scope. Safe to call from background job bodies. Swallows disposal exceptions so abort does not cascade errors when the circuit or context is already disposed.</summary>
    private async Task ReloadWorkspaceDataAfterCancelAsync()
    {
        if (_disposed) return;
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
        try
        {
            await InvokeAsync(() => { if (!_disposed) { ApplySyncStateFromWorkspace(); StateHasChanged(); } });
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
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
            UpdateFilteredRepositories();
            return;
        }
        workspace = w;
        workspaceRepositories = workspace.Repositories
            .OrderByDescending(wr => wr.DependencyLevel ?? int.MinValue)
            .ThenBy(wr => GetRepositoryTypeSortOrder(wr.RepositoryType))
            .ThenByDescending(wr => wr.Dependencies ?? int.MinValue)
            .ToList();
        prByRepositoryId = BuildPrByRepositoryIdFromLinks(workspaceRepositories);
        await LoadMismatchedDependencyLinesAsync();
        UpdateFilteredRepositories();
    }

    private async Task LoadMismatchedDependencyLinesAsync()
    {
        await _loadMismatchedDepsLock.WaitAsync();
        try
        {
            // Fresh scope: circuit-scoped DbContext may be busy (e.g. CheckFileVersions during sync/update).
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var projectRepo = scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();
            var fileVersionService = scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>();

            if (!workspaceRepositories.Any(wr => (wr.UnmatchedDeps ?? 0) > 0))
            {
                _mismatchedDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<(string, string, string)>>();
            }
            else
            {
                try
                {
                    // Use raw sync payloads so tag-pinned repos still get mismatch lines for hover/copy in the badge tooltip.
                    // GetUpdatePlanAsync excludes tag-pinned repos on purpose so Update does not target them.
                    var payloads = await projectRepo.GetSyncDependenciesPayloadAsync(WorkspaceId);
                    var dict = new Dictionary<int, IReadOnlyList<(string PackageId, string CurrentVersion, string NewVersion)>>();
                    foreach (var p in payloads.Where(p => p.ProjectUpdates.Count > 0))
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

            try
            {
                _allDependencyLinesByRepo = await projectRepo.GetPackageDependencyLinesByRepoAsync(WorkspaceId);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not load dependency listing for workspace {WorkspaceId}", WorkspaceId);
                _allDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<(string, string)>>();
            }

            try
            {
                _fileLineStatusByRepo = await fileVersionService.GetFileLineStatusByWorkspaceAsync(WorkspaceId);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not load file line status for workspace {WorkspaceId}", WorkspaceId);
                _fileLineStatusByRepo = new Dictionary<int, IReadOnlyList<WorkspaceFileLineStatus>>();
            }

            try
            {
                var repoVersionMap = workspaceRepositories
                    .Where(r => r.Repository?.RepositoryName != null && !string.IsNullOrEmpty(r.GitVersion))
                    .ToDictionary(r => r.Repository!.RepositoryName!, r => r.GitVersion!, StringComparer.OrdinalIgnoreCase);
                _allFileVersionLinesByRepo = await fileVersionService.GetAllFileVersionLinesByRepoAsync(WorkspaceId, repoVersionMap);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not load file version lines for workspace {WorkspaceId}", WorkspaceId);
                _allFileVersionLinesByRepo = new Dictionary<int, IReadOnlyList<(string, string, string)>>();
            }

            try
            {
                var customDepRepo = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryCustomDependencyRepository>();
                _customDependencyLinesByRepo = await customDepRepo.GetCustomDependencyNamesByRepoAsync(WorkspaceId);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not load custom dependency lines for workspace {WorkspaceId}", WorkspaceId);
                _customDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<string>>();
            }
        }
        finally
        {
            _loadMismatchedDepsLock.Release();
        }
    }

    private IReadOnlyList<WorkspaceFileLineStatus> GetFileLineStatus(int repositoryId) =>
        _fileLineStatusByRepo.TryGetValue(repositoryId, out var lines) ? lines : Array.Empty<WorkspaceFileLineStatus>();

    private bool HasOutOfDateFiles(int repositoryId)
    {
        var link = workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId);
        if (link != null && ((link.OutOfDateFileRepos ?? 0) > 0 || (link.OutOfDateFileLines ?? 0) > 0))
            return true;
        return GetFileLineStatus(repositoryId).Any(s => s.OutOfDateLines > 0);
    }

    private IReadOnlyList<(string FileName, string TokenName, string Version)> GetAllFileVersionLines(int repositoryId) =>
        _allFileVersionLinesByRepo.TryGetValue(repositoryId, out var lines) ? lines : Array.Empty<(string, string, string)>();

    private void OnSearchChanged(ChangeEventArgs e)
    {
        searchTerm = e.Value?.ToString() ?? string.Empty;
        UpdateFilteredRepositories();
        StateHasChanged();
    }

    private void CloseUpdateModal()
    {
        _updateModal = _updateModal with { IsVisible = false };
        StateHasChanged();
    }

    private void CloseUpdateAndPushModal()
    {
        _updateAndPushModal = _updateAndPushModal with { IsVisible = false };
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

    private void ShowDefaultBranchWarning(string message, IReadOnlyList<(string RepoName, string DefaultBranchName)> repoItems, Func<Task> onProceed)
    {
        _defaultBranchWarningModal = _defaultBranchWarningModal with
        {
            IsVisible = true,
            Message = message,
            RepoItems = repoItems,
            PendingAction = onProceed,
        };
        StateHasChanged();
    }

    private void CloseDefaultBranchWarningModal()
    {
        _defaultBranchWarningModal = _defaultBranchWarningModal with
        {
            IsVisible = false,
            PendingAction = null,
        };
        StateHasChanged();
    }

    private async Task OnDefaultBranchWarningProceedAsync()
    {
        var action = _defaultBranchWarningModal.PendingAction;
        CloseDefaultBranchWarningModal();
        if (action != null)
            await action();
    }

    private void ShowSyncToDefaultOptions(string message, IReadOnlyList<(string RepoName, string BranchName, bool HasRemote)> repoItems, Func<bool, bool, Task> onProceed, bool defaultDeleteRemote = true)
    {
        _syncToDefaultOptionsModal = _syncToDefaultOptionsModal with
        {
            IsVisible = true,
            Message = message,
            RepoItems = repoItems,
            DeleteRemoteBranches = defaultDeleteRemote,
            AllowForceDeleteLocalBranch = true,
            PendingAction = onProceed
        };
        StateHasChanged();
    }

    private void CloseSyncToDefaultOptionsModal()
    {
        _syncToDefaultOptionsModal = _syncToDefaultOptionsModal with { IsVisible = false, PendingAction = null };
        StateHasChanged();
    }

    private async Task OnSyncToDefaultOptionsProceedAsync()
    {
        var action = _syncToDefaultOptionsModal.PendingAction;
        var deleteRemote = _syncToDefaultOptionsModal.DeleteRemoteBranches;
        var allowForce = _syncToDefaultOptionsModal.AllowForceDeleteLocalBranch;
        CloseSyncToDefaultOptionsModal();
        if (action != null)
            await action(deleteRemote, allowForce);
    }


    // --- New Pull Request dialog -----------------------------------------------------------

    private NewPullRequestModalState _newPrModal = new();
    private NewFeatureModalState _newFeatureModal = new();
    private OperationErrorModalState _operationErrorModal = new();

    private Task OpenPullRequestDialogForAllRepositoriesAsync()
        => OpenPullRequestDialogCoreAsync(workspaceRepositories);

    private Task OpenPullRequestDialogForRepositoryAsync(WorkspaceRepositoryLink link)
        => OpenPullRequestDialogCoreAsync(new[] { link });

    private Task OpenPullRequestDialogForRepositoriesAsync(IReadOnlyList<WorkspaceRepositoryLink> links)
        => OpenPullRequestDialogCoreAsync(links);

    private Task OpenPullRequestDialogCoreAsync(IEnumerable<WorkspaceRepositoryLink> links)
    {
        var targets = new List<NewPrTargetRepo>();
        foreach (var wr in links)
        {
            var repo = wr.Repository;
            if (repo == null) continue;
            if (wr.IsOnTag) continue;
            if (string.IsNullOrWhiteSpace(wr.BranchName)) continue;
            if (string.IsNullOrWhiteSpace(wr.DefaultBranchName)) continue;
            if (string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal)) continue;
            if ((wr.DefaultBranchAheadCommits ?? 0) <= 0) continue;
            if (!RepositoryUrlHelper.TryParseGitHubOwnerRepo(repo.CloneUrl, out var owner, out var repoName) || owner == null || repoName == null)
                continue;

            var hasOpenPr = wr.PullRequest != null && string.Equals(wr.PullRequest.State, "open", StringComparison.OrdinalIgnoreCase);
            if (hasOpenPr) continue;

            targets.Add(new NewPrTargetRepo(
                RepositoryId: wr.RepositoryId,
                Owner: owner,
                RepositoryName: repoName,
                HeadBranch: wr.BranchName!,
                BaseBranch: wr.DefaultBranchName!,
                CloneUrl: repo.CloneUrl));
        }

        if (targets.Count == 0)
        {
            ToastService.Show("No eligible repositories to create a pull request from.");
            return Task.CompletedTask;
        }

        _newPrModal = new NewPullRequestModalState
        {
            IsVisible = true,
            Targets = targets
        };
        StateHasChanged();
        return Task.CompletedTask;
    }

    private void CloseNewPullRequestModal()
    {
        _newPrModal = _newPrModal with { IsVisible = false };
        JobService.GetJob(PageJobKey)?.Abort();
        StateHasChanged();
    }

    private async Task HandleNewPrOpenInGitHubAsync()
    {
        var targets = _newPrModal.Targets;
        if (targets.Count == 0) return;

        var urls = new List<string>();
        foreach (var t in targets)
        {
            var repoUrl = RepositoryUrlHelper.GetRepositoryUrl(t.CloneUrl);
            if (string.IsNullOrEmpty(repoUrl)) continue;
            urls.Add($"{repoUrl}/compare/{t.BaseBranch}...{Uri.EscapeDataString(t.HeadBranch)}");
        }
        if (urls.Count == 0)
        {
            ToastService.Show("No GitHub URLs are available for the selected repositories.");
            return;
        }

        async Task OpenAsync()
        {
            await JSRuntime.InvokeVoidAsync("graymoonOpenUrls", urls);
        }

        if (urls.Count > 5)
        {
            ShowConfirm($"Do you want to open {urls.Count} repositories in separate tabs?", OpenAsync);
        }
        else
        {
            await OpenAsync();
        }
    }

    private Task HandleCreatePullRequestsAsync(NewPrFormResult form)
    {
        var targets = _newPrModal.Targets;
        if (targets.Count == 0) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(form.Title))
        {
            ToastService.ShowError("Title is required.");
            return Task.CompletedTask;
        }

        var requests = targets.Select(t => new CreatePullRequestRequest
        {
            RepositoryId = t.RepositoryId,
            Owner = t.Owner,
            RepositoryName = t.RepositoryName,
            HeadBranch = t.HeadBranch,
            BaseBranch = t.BaseBranch,
            Title = form.Title,
            Body = form.Body,
            IsDraft = form.IsDraft,
            Reviewers = form.Reviewers,
            TeamReviewers = form.TeamReviewers
        }).ToList();

        var draftSuffix = form.IsDraft ? " as draft" : string.Empty;
        var message = requests.Count == 1
            ? $"Create pull request for {requests[0].RepositoryName}{draftSuffix}?"
            : $"Create {requests.Count} pull requests{draftSuffix}?";

        ShowConfirm(message, () => ExecuteCreatePullRequestsAsync(requests), "Create");
        return Task.CompletedTask;
    }

    private Task ExecuteCreatePullRequestsAsync(IReadOnlyList<CreatePullRequestRequest> requests)
    {
        if (requests.Count == 0 || IsJobRunning)
            return Task.CompletedTask;

        var total = requests.Count;
        _newPrModal = _newPrModal with { IsVisible = false };

        JobService.StartJob(PageJobKey, total == 1 ? "Creating 1 pull request..." : $"Creating {total} pull requests...", async (job, ct) =>
        {
            var progress = new Progress<CreatePullRequestProgress>(p =>
            {
                job.ReportProgress(p.Created == 0
                    ? (p.Total == 1 ? "Creating 1 pull request..." : $"Creating {p.Total} pull requests...")
                    : $"Created {p.Created} of {p.Total} pull requests");
            });

            IReadOnlyList<CreatePullRequestResult> results;
            try
            {
                results = await PullRequestService.CreatePullRequestsAsync(requests, progress, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Create pull requests failed");
                SafeInvoke(() => ToastService.ShowError($"Failed to create pull requests: {ex.Message}"));
                throw;
            }

            var successCount = results.Count(r => r.Success);
            var failedCount = results.Count - successCount;

            SafeInvoke(() =>
            {
                if (successCount > 0 && failedCount == 0)
                {
                    ToastService.Show($"Created {successCount} of {total} pull requests.");
                }
                else if (successCount > 0)
                {
                    ToastService.Show($"Created {successCount} of {total} pull requests.");
                    var firstFailure = results.First(r => !r.Success);
                    ToastService.ShowError($"{firstFailure.RepositoryName}: {firstFailure.ErrorMessage}");
                }
                else
                {
                    var firstFailure = results.FirstOrDefault(r => !r.Success);
                    var msg = firstFailure?.ErrorMessage ?? "Pull request creation failed.";
                    ToastService.ShowError($"No pull requests were created. {msg}");
                }
            });

            var firstSuccess = results.FirstOrDefault(r => r.Success);
            if (successCount == 1 && firstSuccess?.PullRequestUrl is { Length: > 0 } url)
            {
                try
                {
                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await JSRuntime.InvokeVoidAsync("graymoonOpenUrls", new[] { url });
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to open created PR URL");
                }
            }

            var refreshedIds = results.Where(r => r.Success).Select(r => r.RepositoryId).ToList();
            if (refreshedIds.Count > 0)
            {
                try
                {
                    await using var scope = ServiceScopeFactory.CreateAsyncScope();
                    var workspacePrService = scope.ServiceProvider.GetRequiredService<WorkspacePullRequestService>();
                    await workspacePrService.RefreshPullRequestsAsync(WorkspaceId, refreshedIds, cancellationToken: ct);
                    var freshPrs = await workspacePrService.GetPersistedPullRequestsForWorkspaceAsync(WorkspaceId, ct);
                    SafeInvoke(() => { prByRepositoryId = freshPrs; });
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to refresh pull requests after creation");
                }
            }
        });

        return Task.CompletedTask;
    }

    private sealed record NewPullRequestModalState
    {
        public bool IsVisible { get; init; }
        public IReadOnlyList<NewPrTargetRepo> Targets { get; init; } = Array.Empty<NewPrTargetRepo>();
    }

    private void ShowConfirmSyncCommitsLevel(List<int> repositoryIds)
    {
        var filtered = repositoryIds.Where(id => !IsRepoOnTag(id)).ToList();
        if (filtered.Count == 0)
        {
            ToastService.Show("All repositories in this level are on tags; checkout a branch first.");
            return;
        }
        if (filtered.Count <= 1)
            _ = CommitSyncLevelAsync(filtered);
        else
            ShowConfirm($"Do you want to sync commits for {filtered.Count} repositories?", () => CommitSyncLevelAsync(filtered));
    }

    private void ShowConfirmSyncLevel(List<int> repositoryIds)
    {
        var filtered = repositoryIds.Where(id => !IsRepoOnTag(id)).ToList();
        if (filtered.Count == 0)
        {
            ToastService.Show("All repositories in this level are on tags; checkout a branch first.");
            return;
        }
        const int confirmThreshold = 10;
        if (filtered.Count < confirmThreshold)
            _ = SyncLevelAsync(filtered);
        else
            ShowConfirm($"Do you want to sync {filtered.Count} repositories in this level?", () => SyncLevelAsync(filtered));
    }

    private async Task ShowConfirmSyncToDefaultLevel(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0)
            return;

        var nonDefaultRepoIds = repositoryIds
            .Select(id => workspaceRepositories.FirstOrDefault(w => w.RepositoryId == id))
            .Where(wr =>
            {
                if (wr == null || wr.IsOnTag || string.IsNullOrWhiteSpace(wr.BranchName))
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

        await CheckBranchesAndConfirmSyncToDefaultLevel(nonDefaultRepoIds);
    }

    private async Task CheckBranchesAndConfirmSyncToDefaultLevel(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0 || IsJobRunning)
            return;

        _syncToDefaultCheckResults = null;

        try
        {
            await WorkspacePageService.WorkspacePullRequestService.RefreshPullRequestsAsync(WorkspaceId, repositoryIds, force: true);
            await ReloadWorkspaceDataFromFreshScopeAsync();
            ApplySyncStateFromWorkspace();
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "PR refresh before sync-to-default check failed for workspace {WorkspaceId}", WorkspaceId);
        }

        // Synchronous pre-check using persisted state (no agent call needed)
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
        var dialogMessage = safeCount == 1
            ? "This will checkout the default branch, remove the current branch locally, and pull the latest. Uncommitted local changes can block checkout."
            : $"This will sync {safeCount} repositories to their default branch: checkout default, remove the current branch locally, and pull. Uncommitted local changes can block checkout for that repo.";

        JobService.StartJob(PageJobKey,
            safeCount == 1 ? "Fetching latest branch state..." : $"Fetching latest branch state for {safeCount} repositories...",
            async (job, ct) =>
            {
                try
                {
                    var fetchDone = 0;
                    using var fetchSemaphore = new System.Threading.SemaphoreSlim(8);
                    await Task.WhenAll(safeRepoIds.Select(async repoId =>
                    {
                        await fetchSemaphore.WaitAsync(ct);
                        try
                        {
                            await using var scope = ServiceScopeFactory.CreateAsyncScope();
                            var gitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                            await gitService.RefreshBranchesForRepositoryAsync(repoId, WorkspaceId, ct);
                        }
                        finally
                        {
                            fetchSemaphore.Release();
                            var c = Interlocked.Increment(ref fetchDone);
                            job.ReportProgress($"Fetched {c} of {safeRepoIds.Count}...");
                        }
                    }));

                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await RefreshFromSync();

                        // Rebuild check results with fresh HasUpstream from DB
                        _syncToDefaultCheckResults = _syncToDefaultCheckResults?
                            .Select(r =>
                            {
                                var wr2 = workspaceRepositories.FirstOrDefault(w => w.RepositoryId == r.RepoId);
                                return (r.RepoId, r.DefaultAhead, HasUpstream: wr2?.BranchHasUpstream);
                            })
                            .ToList();

                        var repoItems = _syncToDefaultCheckResults?
                            .Select(r =>
                            {
                                var wr2 = workspaceRepositories.FirstOrDefault(w => w.RepositoryId == r.RepoId);
                                return (RepoName: wr2?.Repository?.RepositoryName ?? r.RepoId.ToString(), BranchName: wr2?.BranchName ?? "", HasRemote: r.HasUpstream == true);
                            })
                            .ToList() ?? new();
                        ShowSyncToDefaultOptions(dialogMessage, repoItems, (deleteRemote, allowForce) => SyncToDefaultLevelAsync(safeRepoIds, deleteRemote, allowForce));
                        StateHasChanged();
                    });
                }
                catch (OperationCanceledException)
                {
                    SafeInvoke(() => ToastService.Show("Fetch cancelled."));
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error checking branches for sync to default");
                    SafeInvoke(() => ToastService.Show("Failed to prepare sync to default."));
                    throw;
                }
            });
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
        if (workspace == null || IsJobRunning)
            return;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return;
        }
        try
        {
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            var (payload, _) = await workspaceGitService.GetUpdatePlanAsync(WorkspaceId, new HashSet<int> { repositoryId });
            if (payload == null || payload.Count == 0)
            {
                if (HasOutOfDateFiles(repositoryId))
                {
                    OnFileDependencyBadgeClick(repositoryId);
                    return;
                }
                ToastService.Show("No dependency updates for this repository.");
                return;
            }
            var repoPayload = payload[0];
            var repoName = workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId)?.Repository?.RepositoryName;

            var repo = workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId);
            if (repo != null
                && !string.IsNullOrWhiteSpace(repo.DefaultBranchName)
                && string.Equals(repo.BranchName, repo.DefaultBranchName, StringComparison.Ordinal))
            {
                var hasFiles = HasOutOfDateFiles(repositoryId);
                var warningMsg = hasFiles
                    ? "The following repository is on its default branch. Updating dependencies and file versions will commit changes directly to the default (protected) branch."
                    : "The following repository is on its default branch. Updating dependencies will commit changes directly to the default (protected) branch.";
                ShowDefaultBranchWarning(
                    warningMsg,
                    new[] { (repoName ?? $"repo {repositoryId}", repo.DefaultBranchName!) },
                    () => OpenUpdateSingleRepoModalAsync(repoPayload, repositoryId, repoName));
                return;
            }

            await OpenUpdateSingleRepoModalAsync(repoPayload, repositoryId, repoName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting update plan for repository {RepositoryId}", repositoryId);
            ToastService.Show("Could not load dependency updates.");
        }
    }

    private async Task OpenUpdateSingleRepoModalAsync(SyncDependenciesRepoPayload repoPayload, int repositoryId, string? repoName)
    {
        _updateSingleRepoModal = _updateSingleRepoModal with
        {
            IsVisible = true,
            Payload = repoPayload,
            RepositoryId = repositoryId,
            RepoName = repoName
        };
        await InvokeAsync(StateHasChanged);
    }

    private void CloseUpdateSingleRepositoryDependenciesModal()
    {
        _updateSingleRepoModal = _updateSingleRepoModal with { IsVisible = false, Payload = null };
    }

    private Task OnUpdateSingleRepositoryDependenciesProceedAsync()
    {
        if (_updateSingleRepoModal.Payload == null)
            return Task.CompletedTask;
        var repositoryId = _updateSingleRepoModal.RepositoryId;
        CloseUpdateSingleRepositoryDependenciesModal();

        repositoryErrors.Remove(repositoryId);

        JobService.StartJob(PageJobKey, "Updating repository...", async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var updateHandler = scope.ServiceProvider.GetRequiredService<WorkspaceUpdateHandler>();
                await updateHandler.RunUpdateAsync(
                    WorkspaceId,
                    ct,
                    job.ReportProgress,
                    (repoId, msg) => SafeInvoke(() => { repositoryErrors[repoId] = msg; }),
                    repoIdsToUpdate: new HashSet<int> { repositoryId });

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Update dependencies failed for repository {RepositoryId}", repositoryId);
                SafeInvoke(() => ToastService.Show(ex.Message));
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private void ShowConfirmSyncCommits(int repositoryId)
    {
        ShowConfirm("Do you want to sync commits for this repository?", () => CommitSyncAsync(repositoryId));
    }

    /// <summary>When user clicks the Commits badge and there are incoming commits: run Pull (commit sync) only, with same merge/error handling as CommitSyncAsync.</summary>
    private void OnPullBadgeClickAsync(int repositoryId)
    {
        if (workspace == null || IsJobRunning)
            return;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return;
        }
        _ = CommitSyncAsync(repositoryId);
    }

    /// <summary>When user clicks the not-upstreamed badge: check dependencies. Show modal only if at least one dependency repo needs push; otherwise push directly.</summary>
    private async Task OnPushBadgeClickAsync(int repositoryId, string? branchName)
    {
        if (workspace == null || IsJobRunning)
            return;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return;
        }

        var repo = workspaceRepositories.FirstOrDefault(r => r.RepositoryId == repositoryId);
        if (repo != null
            && !string.IsNullOrWhiteSpace(repo.DefaultBranchName)
            && string.Equals(repo.BranchName, repo.DefaultBranchName, StringComparison.Ordinal))
        {
            var repoName = repo.Repository?.RepositoryName ?? $"repo {repositoryId}";
            ShowDefaultBranchWarning(
                "The following repository is on its default branch. Pushing will commit directly to the default (protected) branch.",
                new[] { (repoName, repo.DefaultBranchName!) },
                () => PushBadgeClickCoreAsync(repositoryId, branchName));
            return;
        }

        await PushBadgeClickCoreAsync(repositoryId, branchName);
    }

    private async Task PushBadgeClickCoreAsync(int repositoryId, string? branchName)
    {
        try
        {
            var repoIdsThatNeedPush = workspaceRepositories
                .Where(wr => !wr.IsOnTag && (wr.OutgoingCommits ?? 0) > 0)
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
    private Task OnPushWithDependenciesProceedAsync(bool synchronizedPush)
    {
        if (_pushWithDependenciesModal.Info == null || workspace == null)
            return Task.CompletedTask;

        var repoIds = _pushWithDependenciesModal.RepoIdsToPush != null
            ? _pushWithDependenciesModal.RepoIdsToPush
            : _pushWithDependenciesModal.Info.DependencyRepoIds.Concat(new[] { _pushWithDependenciesModal.RepoId }).ToHashSet();
        var requiredPackageIds = _pushWithDependenciesModal.Info.PayloadForRepo.RequiredPackages
            .Select(r => r.PackageId?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ClosePushWithDependenciesModal();

        JobService.StartJob(PageJobKey, "Preparing push...", async (job, ct) =>
        {
            try
            {
                await ExecutePushCoreAsync(job, ct, repoIds, synchronizedPush, requiredPackageIds);
            }
            catch (SynchronizedPushNotPossibleException ex)
            {
                // Job completes normally; user confirms to start a new push job without synchronized mode.
                SafeInvoke(() => ShowConfirm(
                    $"Synchronized push could not be completed because {ex.MissingPackagesCount} required package mappings are missing. Check NuGet connector configuration and token, then retry. Continue with normal push?",
                    () =>
                    {
                        JobService.StartJob(PageJobKey, "Preparing push...", (j, c) =>
                            ExecutePushCoreAsync(j, c, repoIds, synchronizedPush: false, requiredPackageIds));
                        return Task.CompletedTask;
                    },
                    confirmButtonText: "Continue"));
            }
        });

        return Task.CompletedTask;
    }

    private async Task<(IReadOnlySet<int> PushRepoIds, IReadOnlySet<string> RequiredPackageIds)?> BuildPushPlanAsync(
        string emptyMessage, CancellationToken ct)
    {
        await using var planScope = ServiceScopeFactory.CreateAsyncScope();
        var planPushHandler = planScope.ServiceProvider.GetRequiredService<WorkspacePushHandler>();
        var planDepService = planScope.ServiceProvider.GetRequiredService<WorkspaceDependencyService>();
        var (payload, hasUnpushed) = await planPushHandler.GetPushPlanAsync(WorkspaceId, workspaceRepositories, ct);
        if (!hasUnpushed)
        {
            SafeInvoke(() => ToastService.Show(emptyMessage));
            return null;
        }
        var taggedRepoIds = workspaceRepositories.Where(wr => wr.IsOnTag).Select(wr => wr.RepositoryId).ToHashSet();
        IReadOnlySet<int> pushRepoIds = payload.Select(p => p.RepoId).Where(id => !taggedRepoIds.Contains(id)).ToHashSet();
        if (pushRepoIds.Count == 0)
        {
            SafeInvoke(() => ToastService.Show(emptyMessage));
            return null;
        }
        var depInfo = await planDepService.GetPushDependencyInfoForRepoSetAsync(WorkspaceId, pushRepoIds, ct);
        IReadOnlySet<string> requiredPackageIds = depInfo?.PayloadForRepo?.RequiredPackages
            .Select(r => r.PackageId?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (pushRepoIds, requiredPackageIds);
    }

    private async Task ExecutePushCoreAsync(
        BackgroundJobHandle job,
        CancellationToken ct,
        IReadOnlySet<int> repoIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds)
    {
        try
        {
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var pushHandler = scope.ServiceProvider.GetRequiredService<WorkspacePushHandler>();
            await pushHandler.RunPushWithDependenciesAsync(
                WorkspaceId,
                repoIds,
                synchronizedPush,
                requiredPackageIds,
                job.ReportProgress,
                ToastService.Show,
                cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            SafeInvoke(() => ToastService.Show("Push cancelled."));
            throw;
        }
        finally
        {
            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                await RefreshFromSync();
            });
        }
    }

    /// <summary>Push with upstream for a single repository (e.g. when user clicks the not-upstreamed badge). Uses the page overlay.</summary>
    private Task PushSingleRepositoryWithUpstreamAsync(int repositoryId, string? branchName)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        JobService.StartJob(PageJobKey, "Setting upstream...", async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var pushHandler = scope.ServiceProvider.GetRequiredService<WorkspacePushHandler>();
                var (success, pushError) = await pushHandler.PushSingleRepositoryWithUpstreamAsync(
                    WorkspaceId,
                    repositoryId,
                    branchName,
                    job.ReportProgress,
                    ct);

                if (success)
                {
                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await RefreshFromSync();
                    });
                }
                else
                {
                    SafeInvoke(() => ToastService.Show(pushError ?? "Push failed."));
                }
            }
            catch (OperationCanceledException)
            {
                SafeInvoke(() => ToastService.Show("Push cancelled."));
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Push with upstream failed for repository {RepositoryId}", repositoryId);
                SafeInvoke(() => ToastService.Show(ex.Message));
                throw;
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>Push button click: get push plan; filter to repos with unpushed commits or no upstream branch; show modal only if at least one dependency repo needs push; otherwise push immediately.</summary>
    private async Task OnPushClickAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
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

            // Exclude repositories pinned to a tag: they have no branch to push.
            var taggedRepoIds = workspaceRepositories.Where(wr => wr.IsOnTag).Select(wr => wr.RepositoryId).ToHashSet();
            var repoIdsWithUnpushed = payload.Select(p => p.RepoId).Where(id => !taggedRepoIds.Contains(id)).ToHashSet();
            if (repoIdsWithUnpushed.Count == 0)
            {
                ToastService.Show("No repositories to push.");
                return;
            }
            var repoIdsThatNeedPush = workspaceRepositories
                .Where(wr => !wr.IsOnTag && (wr.OutgoingCommits ?? 0) > 0)
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
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return;

        var repoIdsWithIncoming = workspaceRepositories
            .Where(wr =>
                !wr.IsOnTag
                && (wr.IncomingCommits ?? 0) > 0
                && !string.IsNullOrWhiteSpace(wr.BranchName)
                && !string.IsNullOrWhiteSpace(wr.DefaultBranchName)
                && string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal))
            .Select(wr => wr.RepositoryId)
            .ToList();
        if (repoIdsWithIncoming.Count == 0)
        {
            ToastService.Show("No repositories with incoming commits to pull.");
            return;
        }
        if (repoIdsWithIncoming.Count == 1)
            _ = CommitSyncAsync(repoIdsWithIncoming[0]);
        else
            _ = CommitSyncLevelAsync(repoIdsWithIncoming);
    }

    /// <summary>Update button click: get update plan; if no updates, toast; else show modal (single vs multi-level).</summary>
    private async Task OnUpdateClickAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return;

        if (workspaceRepositories.All(wr => wr.IsOnTag))
        {
            ToastService.Show("All repositories are on tags; checkout a branch first.");
            return;
        }

        await using var scope = ServiceScopeFactory.CreateAsyncScope();
        var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var (updatePlan, _) = await workspaceGitService.GetUpdatePlanAsync(WorkspaceId);
        var repoIdsWithUpdates = updatePlan.Select(p => p.RepoId).ToHashSet();

        var reposOnDefault = workspaceRepositories
            .Where(wr => !wr.IsOnTag
                && !string.IsNullOrWhiteSpace(wr.DefaultBranchName)
                && string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal)
                && repoIdsWithUpdates.Contains(wr.RepositoryId))
            .ToList();

        if (reposOnDefault.Count > 0)
        {
            var repoItems = reposOnDefault
                .Select(wr => (wr.Repository?.RepositoryName ?? $"repo {wr.RepositoryId}", wr.DefaultBranchName!))
                .ToList();
            ShowDefaultBranchWarning(
                "The following repositories are on their default branch. Update will commit dependency changes directly to the default (protected) branch.",
                repoItems,
                OpenUpdateModalAsync);
            return;
        }

        await OpenUpdateModalAsync();
    }

    private async Task OpenUpdateModalAsync()
    {
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

    private async Task OnUpdateProceedAsync((string? CommitMessage, bool IncludeDeps) args)
    {
        _updateModal = _updateModal with
        {
            IsVisible = false,
            LastCommitMessage = args.CommitMessage,
            LastIncludeDeps = args.IncludeDeps
        };
        await RunUpdateCoreAsync(args.CommitMessage, args.IncludeDeps);
    }

    /// <summary>Runs update (refresh, sync deps, commit per level, refresh version). Overlay shows progress.</summary>
    private Task RunUpdateCoreAsync(string? commitMessage = null, bool includeDepsInCommitMessage = true)
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return Task.CompletedTask;

        errorMessage = null;

        JobService.StartJob(PageJobKey, "Updating dependencies...", async (job, ct) =>
        {
            try
            {
                await using (var updateScope = ServiceScopeFactory.CreateAsyncScope())
                {
                    var updateHandler = updateScope.ServiceProvider.GetRequiredService<WorkspaceUpdateHandler>();
                    await updateHandler.RunUpdateAsync(
                        WorkspaceId,
                        ct,
                        job.ReportProgress,
                        (repoId, msg) => SafeInvoke(() => { repositoryErrors[repoId] = msg; }),
                        repoIdsToUpdate: null,
                        commitMessage: commitMessage,
                        includeDepsInCommitMessage: includeDepsInCommitMessage);
                }

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error updating dependencies for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = "Update failed. The GrayMoon Agent may be offline. Start the Agent and try again.");
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private Task OnUpdateFilesClickAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return Task.CompletedTask;

        errorMessage = null;

        JobService.StartJob(PageJobKey, "Updating file versions...", async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var fileVersionService = scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>();
                var (updated, failed, error, _) = await fileVersionService.UpdateAllVersionsAsync(
                    WorkspaceId, selectedRepositoryIds: null, cancellationToken: ct);

                if (error == null)
                {
                    job.ReportProgress("Checking file versions...");
                    await fileVersionService.CheckAndPersistFileVersionStatusAsync(WorkspaceId, ct, forceFresh: true);
                }

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    if (error == null)
                        await RefreshFromSync();
                    StateHasChanged();
                    if (error != null)
                        errorMessage = error;
                    else if (failed > 0)
                        errorMessage = $"Updated {updated} line(s). {failed} file(s) could not be updated - check logs.";
                    else
                        ToastService.Show(updated > 0 ? "Versions updated in configured files." : "File versions are already up to date.");
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error updating file versions for WorkspaceId={WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = "Failed to update file versions. Please try again.");
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private Task UpdateSingleRepositoryFileVersionsAsync(int repositoryId)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return Task.CompletedTask;
        }

        errorMessage = null;

        JobService.StartJob(PageJobKey, "Updating file versions...", async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var fileVersionService = scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>();
                var repoIds = new HashSet<int> { repositoryId };
                var (updated, failed, error, _) = await fileVersionService.UpdateAllVersionsAsync(
                    WorkspaceId,
                    selectedRepositoryIds: repoIds,
                    filterPatternTokensToSelectedRepositories: false,
                    cancellationToken: ct);

                if (error != null)
                {
                    SafeInvoke(() => errorMessage = error);
                    return;
                }

                job.ReportProgress("Checking file versions...");
                await fileVersionService.CheckAndPersistFileVersionStatusAsync(WorkspaceId, ct, forceFresh: true);

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                    StateHasChanged();
                    if (failed > 0)
                        errorMessage = $"Updated {updated} line(s). {failed} file(s) could not be updated - check logs.";
                    else if (updated > 0)
                        ToastService.Show($"Updated {updated} line(s) in configured files.");
                    else
                        ToastService.Show("File versions are already up to date.");
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error updating file versions for repository {RepositoryId} in workspace {WorkspaceId}", repositoryId, WorkspaceId);
                SafeInvoke(() => errorMessage = "Failed to update file versions. Please try again.");
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private async Task OnUpdateAndPushClickAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return;

        if (workspaceRepositories.All(wr => wr.IsOnTag))
        {
            ToastService.Show("All repositories are on tags; checkout a branch first.");
            return;
        }

        await using var scope = ServiceScopeFactory.CreateAsyncScope();
        var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var (updatePlan, _) = await workspaceGitService.GetUpdatePlanAsync(WorkspaceId);
        var repoIdsWithUpdates = updatePlan.Select(p => p.RepoId).ToHashSet();

        var reposOnDefault = workspaceRepositories
            .Where(wr => !wr.IsOnTag
                && !string.IsNullOrWhiteSpace(wr.DefaultBranchName)
                && string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal)
                && repoIdsWithUpdates.Contains(wr.RepositoryId))
            .ToList();

        if (reposOnDefault.Count > 0)
        {
            var repoItems = reposOnDefault
                .Select(wr => (wr.Repository?.RepositoryName ?? $"repo {wr.RepositoryId}", wr.DefaultBranchName!))
                .ToList();
            ShowDefaultBranchWarning(
                "The following repositories are on their default branch. Update will commit dependency changes directly to the default (protected) branch.",
                repoItems,
                OpenUpdateAndPushModalAsync);
            return;
        }

        await OpenUpdateAndPushModalAsync();
    }

    private async Task OpenUpdateAndPushModalAsync()
    {
        _updateAndPushModal = _updateAndPushModal with { IsVisible = true };
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnUpdateAndPushProceedAsync((string? CommitMessage, bool IncludeDeps) args)
    {
        _updateAndPushModal = _updateAndPushModal with
        {
            IsVisible = false,
            LastCommitMessage = args.CommitMessage,
            LastIncludeDeps = args.IncludeDeps
        };
        await RunUpdateAndPushCoreAsync(args.CommitMessage, args.IncludeDeps);
    }

    private Task RunUpdateAndPushCoreAsync(string? commitMessage = null, bool includeDepsInCommitMessage = true)
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning)
            return Task.CompletedTask;

        errorMessage = null;

        JobService.StartJob(PageJobKey, "Updating dependencies...", async (job, ct) =>
        {
            // Phase 1: update - fresh scope so DbContext does not compete with circuit page loads
            try
            {
                await using (var updateScope = ServiceScopeFactory.CreateAsyncScope())
                {
                    var updateHandler = updateScope.ServiceProvider.GetRequiredService<WorkspaceUpdateHandler>();
                    await updateHandler.RunUpdateAsync(
                        WorkspaceId,
                        ct,
                        job.ReportProgress,
                        (repoId, msg) => SafeInvoke(() => { repositoryErrors[repoId] = msg; }),
                        repoIdsToUpdate: null,
                        commitMessage: commitMessage,
                        includeDepsInCommitMessage: includeDepsInCommitMessage);
                }

                // Unconditional reload so workspaceRepositories is current for Phase 2
                await ReloadWorkspaceDataFromFreshScopeAsync();
                _ = InvokeAsync(() => { if (!_disposed) { ApplySyncStateFromWorkspace(); StateHasChanged(); } });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Update & Push: update failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = "Update failed. The GrayMoon Agent may be offline. Start the Agent and try again.");
                throw;
            }

            // Phase 2: determine push plan using fresh workspaceRepositories loaded above
            job.ReportProgress("Preparing push...");
            IReadOnlySet<int> pushRepoIds;
            IReadOnlySet<string> requiredPackageIds;
            try
            {
                var plan = await BuildPushPlanAsync("Update complete. Nothing to push.", ct);
                if (plan == null) return;
                (pushRepoIds, requiredPackageIds) = plan.Value;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Update & Push: failed to get push plan for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => ShowOperationError("Push Failed", "Update succeeded but push plan could not be determined. The GrayMoon Agent may be offline."));
                throw;
            }

            // Phase 3: execute push
            try
            {
                await ExecutePushCoreAsync(job, ct, pushRepoIds, synchronizedPush: true, requiredPackageIds);
            }
            catch (SynchronizedPushNotPossibleException ex)
            {
                SafeInvoke(() => ShowConfirm(
                    $"Synchronized push could not be completed because {ex.MissingPackagesCount} required package mappings are missing. Check NuGet connector configuration and token, then retry. Continue with normal push?",
                    () =>
                    {
                        JobService.StartJob(PageJobKey, "Preparing push...", async (j, c) =>
                        {
                            await ExecutePushCoreAsync(j, c, pushRepoIds, synchronizedPush: false, requiredPackageIds);
                            await RestorePackagesCoreAsync(j, c);
                        });
                        return Task.CompletedTask;
                    },
                    confirmButtonText: "Continue"));
                return;
            }

            // Phase 4: restore packages after successful push
            await RestorePackagesCoreAsync(job, ct);
        });

        return Task.CompletedTask;
    }

    private async Task HandleDependencyBadgeKeydown(KeyboardEventArgs e, int repositoryId, int unmatchedDeps)
    {
        if ((e.Key != "Enter" && e.Key != " ") || IsJobRunning)
            return;
        if (unmatchedDeps > 0)
            await ShowConfirmUpdateDependenciesAsync(repositoryId, unmatchedDeps);
        else if (HasOutOfDateFiles(repositoryId))
            OnFileDependencyBadgeClick(repositoryId);
    }

    /// <summary>Update dependencies for a single repository only (refresh projects, sync deps, no commit). Same as Update but scoped to one repo.</summary>
    private Task UpdateSingleRepositoryAsync(int repositoryId)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return Task.CompletedTask;
        }

        repositoryErrors.Remove(repositoryId);

        JobService.StartJob(PageJobKey, "Updating repository...", async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                await workspaceGitService.RunUpdateSingleRepositoryAsync(
                    WorkspaceId,
                    repositoryId,
                    onProgressMessage: job.ReportProgress,
                    onRepoError: (repoId, msg) => SafeInvoke(() => { repositoryErrors[repoId] = msg; }),
                    cancellationToken: ct);

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error updating repository {RepositoryId} in workspace {WorkspaceId}", repositoryId, WorkspaceId);
                SafeInvoke(() => { repositoryErrors[repositoryId] = "Update failed. The GrayMoon Agent may be offline. Start the Agent and try again."; });
                throw;
            }
        });

        return Task.CompletedTask;
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
            await WorkspacePageService.WorkspaceRepository.UpdateAsync(WorkspaceId, workspace.Name, _repositoriesModal.SelectedRepositoryIds, workspace.RootPath);
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
            _repositoriesModal.RenameWarnings = result.RenamedRepositories.Count > 0 ? result.RenamedRepositories : null;
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

    private Task RunSyncJobAsync(IReadOnlyList<int>? repositoryIds, string jobLabel, bool skipDependencyLevelPersistence)
    {
        JobService.StartJob(PageJobKey, jobLabel, async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var syncHandler = scope.ServiceProvider.GetRequiredService<WorkspaceSyncHandler>();
                var repoGitInfos = await syncHandler.RunSyncAsync(
                    WorkspaceId,
                    repositoryIds,
                    skipDependencyLevelPersistence,
                    cancellationToken: ct,
                    setProgress: job.ReportProgress,
                    updateRepoGitInfo: (repoId, info) => SafeInvoke(() =>
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
                    }),
                    setRepoSyncStatus: (repoId, status) => repoSyncStatus[repoId] = status);

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
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
                    StateHasChanged();
                });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (AgentNotConnectedException ex)
            {
                Logger.LogError(ex, "Sync failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = $"Sync failed. {ex.Message}");
                throw;
            }
            catch (ConnectorHealthException ex)
            {
                Logger.LogError(ex, "Sync failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = $"Sync failed. {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Sync failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = "Sync failed. An unexpected error occurred. Check the logs for details.");
                throw;
            }
        });
        return Task.CompletedTask;
    }

    private Task SyncAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning) return Task.CompletedTask;
        var skipDependencyLevelPersistence = !string.IsNullOrEmpty(errorMessage);
        errorMessage = null;
        return RunSyncJobAsync(null, "Synchronizing...", skipDependencyLevelPersistence);
    }

    private Task SyncLevelAsync(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0 || IsJobRunning) return Task.CompletedTask;
        errorMessage = null;
        var label = $"Synchronizing {repositoryIds.Count} {(repositoryIds.Count == 1 ? "repository" : "repositories")}...";
        return RunSyncJobAsync(repositoryIds, label, skipDependencyLevelPersistence: true);
    }

    private Task SyncSingleRepoAsync(int repositoryId)
    {
        if (workspace == null || workspaceRepositories.Count == 0 || IsJobRunning) return Task.CompletedTask;
        errorMessage = null;
        return RunSyncJobAsync(new[] { repositoryId }, "Synchronizing repository...", skipDependencyLevelPersistence: true);
    }

    private Task CommitSyncAsync(int repositoryId)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return Task.CompletedTask;
        }

        errorMessage = null;

        JobService.StartJob(PageJobKey, "Synchronizing commits...", async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var commitSyncHandler = scope.ServiceProvider.GetRequiredService<WorkspaceCommitSyncHandler>();
                await commitSyncHandler.CommitSyncAsync(
                    WorkspaceId,
                    repositoryId,
                    ct,
                    msg => { job.ReportProgress(msg); return Task.CompletedTask; },
                    (id, err) => SafeInvoke(() =>
                    {
                        if (err is null)
                            repositoryErrors.Remove(id);
                        else
                            repositoryErrors[id] = err;
                    }),
                    msg => SafeInvoke(() => errorMessage = msg));

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private Task CommitSyncLevelAsync(List<int> repositoryIds)
    {
        if (workspace == null || IsJobRunning || repositoryIds == null || repositoryIds.Count == 0)
            return Task.CompletedTask;

        repositoryIds = repositoryIds.Where(id => !IsRepoOnTag(id)).ToList();
        if (repositoryIds.Count == 0)
        {
            ToastService.Show("All repositories are on tags; checkout a branch first.");
            return Task.CompletedTask;
        }

        errorMessage = null;

        JobService.StartJob(PageJobKey, "Synchronizing commits...", async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var commitSyncHandler = scope.ServiceProvider.GetRequiredService<WorkspaceCommitSyncHandler>();
                await commitSyncHandler.CommitSyncLevelAsync(
                    WorkspaceId,
                    repositoryIds,
                    ct,
                    (completed, total) => { job.ReportProgress($"Synchronized commits {completed} of {total}"); return Task.CompletedTask; },
                    (id, err) => SafeInvoke(() =>
                    {
                        if (err is null)
                            repositoryErrors.Remove(id);
                        else
                            repositoryErrors[id] = err;
                    }),
                    msg => SafeInvoke(() => errorMessage = msg));

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
        });

        return Task.CompletedTask;
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
            RepositoryUrl = null,
            InitialTab = null
        };
    }

    private void ShowSwitchBranchModalOnTagsTab(WorkspaceRepositoryLink link)
    {
        var wr = workspaceRepositories.FirstOrDefault(r => r.RepositoryId == link.RepositoryId);
        var repo = wr?.Repository;
        if (repo == null)
            return;

        _switchBranchModal = _switchBranchModal with
        {
            IsVisible = true,
            RepositoryId = link.RepositoryId,
            RepositoryName = repo.RepositoryName,
            CurrentBranch = null,
            RepositoryUrl = repo.CloneUrl,
            InitialTab = "tags"
        };
        StateHasChanged();
    }

    /// <summary>When every workspace repo has the same non-empty <see cref="WorkspaceRepositoryLink.BranchName"/>, returns that name; otherwise null.</summary>
    private static string? GetUnifiedWorkspaceCurrentBranch(IReadOnlyList<WorkspaceRepositoryLink> links)
    {
        if (links.Count == 0)
            return null;
        string? first = null;
        foreach (var link in links)
        {
            var name = link.BranchName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return null;
            first ??= name;
            if (!string.Equals(first, name, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return first;
    }

    private async Task ShowBranchModalAsync(string initialTab = "newbranch")
    {
        if (workspace == null || workspaceRepositories.Count == 0)
            return;
        try
        {
            await LoadCommonBranchesForBranchModalAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load common branches for branch modal");
        }
        _branchModal = _branchModal with
        {
            IsVisible = true,
            WorkspaceUnifiedCurrentBranch = GetUnifiedWorkspaceCurrentBranch(workspaceRepositories),
            InitialTab = string.Equals(initialTab, "switchbranch", StringComparison.OrdinalIgnoreCase) ? "switchbranch" : "newbranch"
        };
        StateHasChanged();
    }

    private void CloseBranchModal()
    {
        _branchModal = _branchModal with { IsVisible = false };
    }

    private async Task ShowNewFeatureModalAsync()
    {
        if (workspace == null || workspaceRepositories.Count == 0)
            return;
        try
        {
            await LoadCommonBranchesForBranchModalAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load common branches for new feature modal");
        }
        _newFeatureModal = _newFeatureModal with
        {
            IsVisible = true,
            WorkspaceName = workspace?.Name,
            CommonBranchNames = _branchModal.CommonBranchNames,
            DefaultDisplayText = _branchModal.DefaultDisplayText,
        };
        StateHasChanged();
    }

    private void CloseNewFeatureModal()
    {
        _newFeatureModal = _newFeatureModal with { IsVisible = false };
    }

    private void ShowOperationError(string title, string message)
    {
        _operationErrorModal = _operationErrorModal with { IsVisible = true, Title = title, Message = message };
        _ = InvokeAsync(StateHasChanged);
    }

    private void CloseOperationErrorModal()
    {
        _operationErrorModal = _operationErrorModal with { IsVisible = false };
        StateHasChanged();
    }

    private Task HandleNewFeatureCreateAsync(NewFeatureRequest request)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        CloseNewFeatureModal();
        errorMessage = null;

        var tagFilteredRepoIds = request.SkipReposOnTags
            ? workspaceRepositories.Where(wr => !wr.IsOnTag).Select(wr => wr.RepositoryId).ToHashSet()
            : (IReadOnlySet<int>?)null;

        // Phases 1 + 2: branch creation (hooks suppressed, state persisted inline) then optional update.
        // NewFeatureOrchestrator guarantees all CheckedOutTag fields are null before the update
        // runs, so DependencyUpdateOrchestrator never skips previously tag-pinned repos.
        JobService.StartJob(PageJobKey, "Creating branches...", async (job, ct) =>
        {
            try
            {
                // Fresh scope so DbContext does not compete with circuit page loads
                await using (var orchestratorScope = ServiceScopeFactory.CreateAsyncScope())
                {
                    var orchestrator = orchestratorScope.ServiceProvider.GetRequiredService<NewFeatureOrchestrator>();
                    await orchestrator.RunAsync(
                        WorkspaceId,
                        request.NewBranchName,
                        request.BaseBranch,
                        tagFilteredRepoIds,
                        request.UpdateDependencies,
                        commitMessage: null,
                        setProgress: msg => job.ReportProgress(msg),
                        reportBranchProgress: (completed, total) =>
                        {
                            job.ReportProgress($"Created {completed} of {total} branches");
                        },
                        setRepositoryError: (repoId, msg) => SafeInvoke(() => { repositoryErrors[repoId] = msg; }),
                        ct);
                }

                // Unconditional reload so workspaceRepositories is current for Phase 3
                await ReloadWorkspaceDataFromFreshScopeAsync();
                _ = InvokeAsync(() => { if (!_disposed) { ApplySyncStateFromWorkspace(); StateHasChanged(); } });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "New Feature: orchestration failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => ShowOperationError("New Feature Failed", "Could not complete the New Feature workflow. The GrayMoon Agent may be offline or a dependency update failed. Check individual repository errors for details."));
                throw;
            }

            if (!request.PushChanges)
                return;

            // Phase 3: determine push plan and execute push
            job.ReportProgress("Preparing push...");
            IReadOnlySet<int> pushRepoIds;
            IReadOnlySet<string> requiredPackageIds;
            try
            {
                var plan = await BuildPushPlanAsync("No repositories to push.", ct);
                if (plan == null) return;
                (pushRepoIds, requiredPackageIds) = plan.Value;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "New Feature: failed to get push plan for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => ShowOperationError("Push Failed", "Could not determine push plan. The GrayMoon Agent may be offline."));
                throw;
            }

            try
            {
                await ExecutePushCoreAsync(job, ct, pushRepoIds, synchronizedPush: true, requiredPackageIds);
            }
            catch (SynchronizedPushNotPossibleException ex)
            {
                SafeInvoke(() => ShowOperationError("Push Failed",
                    $"Synchronized push could not complete: {ex.MissingPackagesCount} required package mapping(s) are missing. Check NuGet connector configuration and token, then retry."));
                return;
            }

            // Phase 4: restore packages after successful push
            await RestorePackagesCoreAsync(job, ct);
        });

        return Task.CompletedTask;
    }

    private async Task LoadCommonBranchesForBranchModalAsync(CancellationToken cancellationToken)
    {
        var data = await WorkspaceBranchHandler.GetCommonBranchesAsync(
            WorkspaceId,
            ApiBaseUrl,
            cancellationToken);

        if (data == null)
            return;

        var commonLocal = data.CommonLocalBranchNames ?? data.CommonBranchNames ?? new List<string>();
        var commonRemote = data.CommonRemoteBranchNames ?? new List<string>();
        _branchModal = _branchModal with
        {
            CommonBranchNames = commonLocal,
            CommonLocalBranchNames = commonLocal,
            CommonRemoteBranchNames = commonRemote,
            DefaultDisplayText = data.DefaultDisplayText ?? "multiple",
            WorkspaceUnifiedCurrentBranch = GetUnifiedWorkspaceCurrentBranch(workspaceRepositories)
        };
    }

    private Task FetchCommonBranchesAcrossWorkspaceAsync()
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        var repoIds = workspaceRepositories
            .Select(wr => wr.RepositoryId)
            .Distinct()
            .ToList();
        if (repoIds.Count == 0)
            return Task.CompletedTask;

        JobService.StartJob(PageJobKey, "Fetching branches...", async (job, ct) =>
        {
            try
            {
                var fetchAllDone = 0;
                var successCount = 0;
                var failureCount = 0;
                using var fetchAllSemaphore = new System.Threading.SemaphoreSlim(8);
                await Task.WhenAll(repoIds.Select(async repoId =>
                {
                    await fetchAllSemaphore.WaitAsync(ct);
                    try
                    {
                        await using var scope = ServiceScopeFactory.CreateAsyncScope();
                        var gitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                        var ok = await gitService.RefreshBranchesForRepositoryAsync(repoId, WorkspaceId, ct);
                        if (ok) Interlocked.Increment(ref successCount);
                        else Interlocked.Increment(ref failureCount);
                    }
                    finally
                    {
                        fetchAllSemaphore.Release();
                        var c = Interlocked.Increment(ref fetchAllDone);
                        job.ReportProgress($"Fetched branches in {c} of {repoIds.Count} repositories...");
                    }
                }));

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                    await LoadCommonBranchesForBranchModalAsync(CancellationToken.None);
                    StateHasChanged();
                });

                if (failureCount > 0)
                    SafeInvoke(() => ToastService.Show($"Fetched branches for {successCount} repositories. {failureCount} failed."));
            }
            catch (OperationCanceledException)
            {
                SafeInvoke(() => ToastService.Show("Fetch branches cancelled."));
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error fetching branches across workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => ToastService.Show("Failed to fetch branches across workspace."));
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private Task CheckoutCommonBranchAcrossWorkspaceAsync((string BranchName, bool SkipReposOnTags) args)
    {
        var (branchName, skipReposOnTags) = args;
        if (workspace == null || string.IsNullOrWhiteSpace(branchName) || IsJobRunning)
            return Task.CompletedTask;

        var repoIds = workspaceRepositories
            .Where(wr => !skipReposOnTags || !wr.IsOnTag)
            .Select(wr => wr.RepositoryId)
            .Distinct()
            .ToList();
        if (repoIds.Count == 0)
            return Task.CompletedTask;

        JobService.StartJob(PageJobKey, "Checking out...", async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var branchHandler = scope.ServiceProvider.GetRequiredService<WorkspaceBranchHandler>();
                var result = await branchHandler.CheckoutBranchForWorkspaceAsync(
                    WorkspaceId,
                    repoIds,
                    branchName,
                    ApiBaseUrl,
                    (completed, total) =>
                    {
                        job.ReportProgress(completed <= 0
                            ? "Checking out..."
                            : $"Checked out {completed} of {total} branches");
                    },
                    ct);

                SafeInvoke(() =>
                {
                    foreach (var repoId in repoIds)
                    {
                        if (result.ErrorsByRepositoryId.TryGetValue(repoId, out var error))
                            repositoryErrors[repoId] = error;
                        else
                            repositoryErrors.Remove(repoId);
                    }
                });

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });

                if (result.FailureCount > 0)
                    SafeInvoke(() => ToastService.Show($"Checked out branch in {result.SuccessCount} repositories. {result.FailureCount} failed."));
            }
            catch (OperationCanceledException)
            {
                SafeInvoke(() => ToastService.Show("Checkout cancelled."));
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error checking out branch {BranchName} across workspace {WorkspaceId}", branchName, WorkspaceId);
                SafeInvoke(() => ToastService.Show("Failed to check out branch across workspace."));
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private Task CreateBranchesAsync((string NewBranchName, string BaseBranch, bool SkipReposOnTags) args)
    {
        var (newBranchName, baseBranch, skipReposOnTags) = args;
        if (workspace == null || string.IsNullOrWhiteSpace(newBranchName) || IsJobRunning)
            return Task.CompletedTask;

        CloseBranchModal();
        errorMessage = null;

        var tagFilteredRepoIds = skipReposOnTags
            ? workspaceRepositories.Where(wr => !wr.IsOnTag).Select(wr => wr.RepositoryId).ToHashSet()
            : (IReadOnlySet<int>?)null;

        JobService.StartJob(PageJobKey, "Creating branches...", async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var branchHandler = scope.ServiceProvider.GetRequiredService<WorkspaceBranchHandler>();
                await branchHandler.CreateBranchesAsync(
                    WorkspaceId,
                    newBranchName,
                    baseBranch,
                    tagFilteredRepoIds,
                    (completed, total) =>
                    {
                        job.ReportProgress($"Created {completed} of {total} branches");
                    },
                    syncState: false,
                    cancellationToken: ct);

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error creating branches for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = "Create branches failed. The GrayMoon Agent may be offline. Start the Agent and try again.");
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private async Task OnBranchChangedAsync()
    {
        // Refresh workspace data to show updated branch
        await RefreshFromSync();
    }

    private Task CreateSingleBranchAsync((int RepositoryId, string? RepositoryName, string NewBranchName, string BaseBranch, bool SetUpstream) request)
    {
        var (repositoryId, repositoryName, newBranchName, baseBranch, setUpstream) = request;
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        CloseSwitchBranchModal();
        errorMessage = null;

        JobService.StartJob(PageJobKey, "Creating branch...", async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var branchHandler = scope.ServiceProvider.GetRequiredService<WorkspaceBranchHandler>();
                var (success, err) = await branchHandler.CreateSingleBranchAsync(
                    WorkspaceId,
                    repositoryId,
                    newBranchName,
                    baseBranch,
                    setUpstream,
                    ApiBaseUrl,
                    ct);

                if (!success)
                {
                    SafeInvoke(() => errorMessage = err ?? "Create branch failed. The GrayMoon Agent may be offline. Start the Agent and try again.");
                }
                else
                {
                    if (err != null)
                        SafeInvoke(() => errorMessage = err);

                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await RefreshFromSync();
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error creating branch for repository {RepositoryId}", repositoryId);
                SafeInvoke(() => errorMessage = "An error occurred while creating branch.");
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private async Task SyncToDefaultFromModalAsync((int RepositoryId, string? RepositoryName, string CurrentBranchName, string DefaultBranch) request)
    {
        var (repositoryId, repositoryName, currentBranchName, defaultBranch) = request;
        if (workspace == null || IsJobRunning)
            return;
        if (string.IsNullOrWhiteSpace(repositoryName))
            return;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            CloseSwitchBranchModal();
            return;
        }

        CloseSwitchBranchModal();

        try
        {
            // Use persisted workspace link state (updated by hooks); no agent GetCommitCounts call.
            var wr = workspaceRepositories.FirstOrDefault(w => w.RepositoryId == repositoryId);
            var defaultAhead = wr?.DefaultBranchAheadCommits ?? 0;
            var hasUpstream = wr?.BranchHasUpstream == true;

            if (defaultAhead > 0)
            {
                try
                {
                    await WorkspacePageService.WorkspacePullRequestService.RefreshPullRequestsAsync(WorkspaceId, new[] { repositoryId }, force: true);
                    await ReloadWorkspaceDataFromFreshScopeAsync();
                    ApplySyncStateFromWorkspace();
                    await InvokeAsync(StateHasChanged);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "PR refresh before sync-to-default check failed for RepositoryId={RepositoryId}", repositoryId);
                }
            }

            if (defaultAhead > 0 && !IsPrMergedForRepo(repositoryId))
            {
                ToastService.Show("Skipped sync to default: commits ahead of default branch and PR is not merged.");
                return;
            }

            if (hasUpstream)
            {
                var branchName = wr?.BranchName ?? currentBranchName;
                ShowSyncToDefaultOptions(
                    "This will checkout the default branch, remove the current branch locally, and pull the latest.",
                    [(repositoryName!, branchName, true)],
                    (deleteRemote, allowForce) => SyncToDefaultSingleRepoAfterCheckAsync(repositoryId, repositoryName, currentBranchName, deleteRemote, defaultBranch, allowForce));
            }
            else
            {
                await SyncToDefaultSingleRepoAfterCheckAsync(repositoryId, repositoryName, currentBranchName, deleteRemoteBranch: false, defaultBranch, allowForceDeleteLocalBranch: true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error preparing sync to default (repository {RepositoryId})", repositoryId);
            ToastService.Show("Failed to prepare sync to default.");
        }
    }

    private Task SyncToDefaultSingleRepoAfterCheckAsync(int repositoryId, string repositoryName, string currentBranchName, bool deleteRemoteBranch = false, string? defaultBranchName = null, bool allowForceDeleteLocalBranch = true)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        var message = string.IsNullOrWhiteSpace(defaultBranchName)
            ? "Synchronizing to default branch..."
            : $"Synchronizing to {defaultBranchName}...";

        JobService.StartJob(PageJobKey, message, async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var gitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                var (success, errMsg) = await gitService.SyncToDefaultDirectAsync(
                    WorkspaceId,
                    repositoryId,
                    currentBranchName,
                    deleteRemoteBranch,
                    allowForceDeleteLocalBranch,
                    ct);

                if (success)
                {
                    SafeInvoke(() => repositoryErrors.Remove(repositoryId));
                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await RefreshFromSync();
                    });
                }
                else if (errMsg != null)
                {
                    SafeInvoke(() => { repositoryErrors[repositoryId] = errMsg; });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error syncing to default branch for repository {RepositoryId}", repositoryId);
                SafeInvoke(() => { repositoryErrors[repositoryId] = "An error occurred while syncing to default branch. The GrayMoon Agent may be offline."; });
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private Task SyncToDefaultLevelAsync(List<int> repositoryIds, bool deleteRemoteBranch = false, bool allowForceDeleteLocalBranch = true)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0 || IsJobRunning)
            return Task.CompletedTask;

        repositoryIds = repositoryIds.Where(id => !IsRepoOnTag(id)).ToList();
        if (repositoryIds.Count == 0)
        {
            ToastService.Show("All repositories are on tags; checkout a branch first.");
            return Task.CompletedTask;
        }

        var checkResults = _syncToDefaultCheckResults;
        _syncToDefaultCheckResults = null;
        errorMessage = null;

        JobService.StartJob(PageJobKey, "Synchronizing to default branch...", async (job, ct) =>
        {
            var total = repositoryIds.Count;
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
                            job.ReportProgress($"Synchronized {c} of {total} to default branch");
                        return (repositoryId, true, (string?)null);
                    }

                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var repoHasRemote = !resultByRepo.TryGetValue(repositoryId, out var repoCheck) || repoCheck.HasUpstream == true;
                        await using var taskScope = ServiceScopeFactory.CreateAsyncScope();
                        var taskGitService = taskScope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                        var (success, errMsg) = await taskGitService.SyncToDefaultDirectAsync(
                            WorkspaceId,
                            repositoryId,
                            currentBranchName,
                            deleteRemoteBranch && repoHasRemote,
                            allowForceDeleteLocalBranch,
                            ct);

                        return (repositoryId, success, errMsg);
                    }
                    finally
                    {
                        semaphore.Release();
                        var c = Interlocked.Increment(ref completedCount);
                        if (total > 1)
                            job.ReportProgress($"Synchronized {c} of {total} to default branch");
                    }
                });

                var results = await Task.WhenAll(tasks);
                SafeInvoke(() =>
                {
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
                });

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error syncing to default branch for level");
                SafeInvoke(() => errorMessage = "An error occurred while syncing to default branch. The GrayMoon Agent may be offline.");
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private Task CheckoutBranchAsync((int RepositoryId, string BranchName, bool IsTag) request)
    {
        var (repositoryId, branchName, isTag) = request;
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        errorMessage = null;

        JobService.StartJob(PageJobKey, isTag ? "Checking out tag..." : "Checking out branch...", async (job, ct) =>
        {
            try
            {
                await using var scope = ServiceScopeFactory.CreateAsyncScope();
                var branchHandler = scope.ServiceProvider.GetRequiredService<WorkspaceBranchHandler>();
                var (success, errMsg) = await branchHandler.CheckoutBranchAsync(
                    WorkspaceId,
                    repositoryId,
                    branchName,
                    isTag,
                    ApiBaseUrl,
                    ct);

                SafeInvoke(() =>
                {
                    if (success)
                        repositoryErrors.Remove(repositoryId);
                    else
                        repositoryErrors[repositoryId] = errMsg ?? (isTag ? "Failed to checkout tag." : "Failed to checkout branch.");
                });

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error checking out {Kind} for repository {RepositoryId}", isTag ? "tag" : "branch", repositoryId);
                SafeInvoke(() => { repositoryErrors[repositoryId] = isTag
                    ? "Failed to checkout tag. The GrayMoon Agent may be offline. Start the Agent and try again."
                    : "Failed to checkout branch. The GrayMoon Agent may be offline. Start the Agent and try again."; });
                throw;
            }
        });

        return Task.CompletedTask;
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

    private async Task CopyDependenciesToClipboard(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", text);
            ToastService.Show("Dependency list copied to the clipboard");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to copy dependency list to clipboard");
            ToastService.Show("Could not copy to clipboard.");
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

    private void OnFileDependencyBadgeClick(int repositoryId)
    {
        clickedDependencyBadges.Add(repositoryId);
        _ = UpdateSingleRepositoryFileVersionsAsync(repositoryId);
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
            UpdateFilteredRepositories();
            StateHasChanged();
        }
    }

    private void ClearSearchFilter()
    {
        searchTerm = string.Empty;
        UpdateFilteredRepositories();
        StateHasChanged();
    }

    private const int TableColSpan = 4;

    private PullRequestInfo? GetPrInfoForRepository(int repositoryId)
    {
        return prByRepositoryId.TryGetValue(repositoryId, out var pr) ? pr : null;
    }

    private IReadOnlyList<string> GetOpenPrUrlsForGroup(IEnumerable<WorkspaceRepositoryLink> group)
    {
        var urls = new List<string>();
        foreach (var wr in group)
        {
            if (!prByRepositoryId.TryGetValue(wr.RepositoryId, out var pr) || pr == null) continue;
            if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(pr.HtmlUrl))
                urls.Add(pr.HtmlUrl);
        }
        return urls;
    }

    private IReadOnlyDictionary<string, string> GetOpenPrPullMapForGroup(IEnumerable<WorkspaceRepositoryLink> group)
    {
        var map = new Dictionary<string, string>();
        foreach (var wr in group)
        {
            if (wr.Repository == null) continue;
            var baseUrl = RepositoryUrlHelper.GetRepositoryUrl(wr.Repository.CloneUrl);
            if (string.IsNullOrEmpty(baseUrl)) continue;
            if (!prByRepositoryId.TryGetValue(wr.RepositoryId, out var pr) || pr == null) continue;
            if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(pr.HtmlUrl))
                map[baseUrl] = pr.HtmlUrl;
        }
        return map;
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

    private IReadOnlyList<(string PackageId, string Version)> GetAllDependencyLines(int repositoryId)
    {
        return _allDependencyLinesByRepo.GetValueOrDefault(repositoryId) ?? Array.Empty<(string PackageId, string Version)>();
    }

    private IReadOnlyList<string> GetCustomDependencyLines(int repositoryId)
    {
        return _customDependencyLinesByRepo.GetValueOrDefault(repositoryId) ?? Array.Empty<string>();
    }

    private async Task ShowCustomDependenciesModalAsync(int repositoryId)
    {
        if (workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId) is not { } link)
            return;

        if (link.IsOnTag)
        {
            ToastService.Show("Repository is on a tag; checkout a branch first.");
            return;
        }

        try
        {
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var projectRepo = scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();
            var customDepRepo = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryCustomDependencyRepository>();

            var lockedIds = await projectRepo.GetImplicitReferencedRepoIdsAsync(WorkspaceId, repositoryId);
            var savedCustomIds = await customDepRepo.GetCustomReferencedRepositoryIdsAsync(WorkspaceId, repositoryId);

            _customDependenciesModal = new CustomDependenciesModalState
            {
                IsVisible = true,
                DependentRepositoryId = repositoryId,
                DependentWorkspaceRepositoryId = link.WorkspaceRepositoryId,
                DependentRepoName = link.Repository?.RepositoryName,
                LockedReferencedRepoIds = lockedIds,
                SelectedCustomRepoIds = new HashSet<int>(savedCustomIds),
                Repositories = workspaceRepositories
                    .Where(wr => wr.Repository?.RepositoryName != null)
                    .Select(wr => new CustomDependenciesModal.CustomDependencyRepoEntry(
                        wr.RepositoryId,
                        wr.Repository!.RepositoryName!))
                    .ToList(),
                ErrorMessage = null,
                IsSaving = false
            };
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not open custom dependencies dialog for repo {RepositoryId}", repositoryId);
            ToastService.ShowError("Could not open custom dependencies dialog.");
        }
    }

    private void CloseCustomDependenciesModal()
    {
        _customDependenciesModal = new CustomDependenciesModalState();
        StateHasChanged();
    }

    private async Task SaveCustomDependenciesAsync()
    {
        if (!_customDependenciesModal.IsVisible || _customDependenciesModal.IsSaving)
            return;

        var dependentRepoId = _customDependenciesModal.DependentRepositoryId;
        var locked = _customDependenciesModal.LockedReferencedRepoIds;
        var selected = _customDependenciesModal.SelectedCustomRepoIds
            .Where(id => !locked.Contains(id) && id != dependentRepoId)
            .ToHashSet();

        _customDependenciesModal.IsSaving = true;
        _customDependenciesModal.ErrorMessage = null;
        StateHasChanged();

        try
        {
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var customDepRepo = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryCustomDependencyRepository>();
            var gitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

            await customDepRepo.ReplaceCustomDependenciesForDependentAsync(WorkspaceId, dependentRepoId, selected);
            await gitService.RecomputeAndBroadcastWorkspaceSyncedAsync(WorkspaceId);
            await ReloadWorkspaceDataFromFreshScopeAsync();

            CloseCustomDependenciesModal();
            ToastService.Show("Custom dependencies saved.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to save custom dependencies for repo {RepositoryId}", dependentRepoId);
            _customDependenciesModal.IsSaving = false;
            _customDependenciesModal.ErrorMessage = "Failed to save custom dependencies. Please try again.";
            StateHasChanged();
        }
    }

    private List<WorkspaceRepositoryLink> GetFilteredWorkspaceRepositories()
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return workspaceRepositories;
        }

        return workspaceRepositories
            .Where(wr => WorkspaceRepositoryLinkSearchMatcher.Matches(wr, searchTerm, repoSyncStatus))
            .ToList();
    }

    private sealed record ConfirmModalState
    {
        public bool IsVisible { get; init; }
        public string Message { get; init; } = "";
        public string ButtonText { get; init; } = "Yes";
        public Func<Task>? PendingAction { get; init; }
    }

    private sealed record DefaultBranchWarningModalState
    {
        public bool IsVisible { get; init; }
        public string Message { get; init; } = "";
        public IReadOnlyList<(string RepoName, string DefaultBranchName)> RepoItems { get; init; } = Array.Empty<(string, string)>();
        public Func<Task>? PendingAction { get; init; }
    }

    private sealed record SyncToDefaultOptionsModalState
    {
        public bool IsVisible { get; init; }
        public string Message { get; init; } = "";
        public IReadOnlyList<(string RepoName, string BranchName, bool HasRemote)> RepoItems { get; init; } = Array.Empty<(string, string, bool)>();
        public bool DeleteRemoteBranches { get; init; } = true;
        public bool AllowForceDeleteLocalBranch { get; init; } = true;
        public Func<bool, bool, Task>? PendingAction { get; init; }
    }

    private sealed class RepositoriesModalState
    {
        public bool IsVisible { get; set; }
        public string? ErrorMessage { get; set; }
        public IReadOnlyList<RenamedRepositoryInfo>? RenameWarnings { get; set; }
        public List<GitHubRepositoryEntry>? Repositories { get; set; }
        public HashSet<int> SelectedRepositoryIds { get; set; } = new();
        public bool IsSaving { get; set; }
        public bool IsFetching { get; set; }
        public int? FetchedRepositoryCount { get; set; }
        public bool HasConnectors { get; set; }
    }

    private sealed class CustomDependenciesModalState
    {
        public bool IsVisible { get; set; }
        public int DependentRepositoryId { get; set; }
        public int DependentWorkspaceRepositoryId { get; set; }
        public string? DependentRepoName { get; set; }
        public IReadOnlySet<int> LockedReferencedRepoIds { get; set; } = new HashSet<int>();
        public HashSet<int> SelectedCustomRepoIds { get; set; } = new();
        public IReadOnlyList<CustomDependenciesModal.CustomDependencyRepoEntry> Repositories { get; set; } = Array.Empty<CustomDependenciesModal.CustomDependencyRepoEntry>();
        public bool IsSaving { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private sealed record BranchModalState
    {
        public bool IsVisible { get; init; }
        public IReadOnlyList<string> CommonBranchNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> CommonLocalBranchNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> CommonRemoteBranchNames { get; init; } = Array.Empty<string>();
        public string DefaultDisplayText { get; init; } = "multiple";
        /// <summary>Local branch name when every linked repo reports the same current branch; otherwise null.</summary>
        public string? WorkspaceUnifiedCurrentBranch { get; init; }
        public string InitialTab { get; init; } = "newbranch";
    }

    private sealed record SwitchBranchModalState
    {
        public bool IsVisible { get; init; }
        public int RepositoryId { get; init; }
        public string? RepositoryName { get; init; }
        public string? CurrentBranch { get; init; }
        public string? RepositoryUrl { get; init; }
        public string? InitialTab { get; init; }
    }

    private sealed record UpdateModalState
    {
        public bool IsVisible { get; init; }
        public string? LastCommitMessage { get; init; }
        public bool LastIncludeDeps { get; init; } = true;
    }

    private sealed record UpdateSingleRepoDependenciesModalState
    {
        public bool IsVisible { get; init; }
        public SyncDependenciesRepoPayload? Payload { get; init; }
        public int RepositoryId { get; init; }
        public string? RepoName { get; init; }
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

    private sealed record NewFeatureModalState
    {
        public bool IsVisible { get; init; }
        public string? WorkspaceName { get; init; }
        public IReadOnlyList<string> CommonBranchNames { get; init; } = Array.Empty<string>();
        public string DefaultDisplayText { get; init; } = "multiple";
    }

    private sealed record OperationErrorModalState
    {
        public bool IsVisible { get; init; }
        public string Title { get; init; } = "Operation Failed";
        public string Message { get; init; } = "";
    }

    private sealed record UndoPushModalState
    {
        public bool IsVisible { get; init; }
        public IReadOnlyList<(string RepoName, int OutgoingCommits)> Repos { get; init; } = Array.Empty<(string, int)>();
    }

    private Task OnUndoPushClickAsync()
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        var repos = workspaceRepositories
            .Where(wr => !wr.IsOnTag && (wr.OutgoingCommits ?? 0) > 0)
            .Select(wr => (RepoName: wr.Repository?.RepositoryName ?? wr.RepositoryId.ToString(), OutgoingCommits: wr.OutgoingCommits ?? 0))
            .ToList();

        if (repos.Count == 0)
        {
            ToastService.Show("No repositories with outgoing commits.");
            return Task.CompletedTask;
        }

        _undoPushModal = _undoPushModal with { IsVisible = true, Repos = repos };
        StateHasChanged();
        return Task.CompletedTask;
    }

    private Task OnUndoPushProceedAsync(bool keepChanges)
    {
        CloseUndoPushModal();

        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        var reposSnapshot = workspaceRepositories.ToList();
        errorMessage = null;

        JobService.StartJob(PageJobKey, "Resetting outgoing commits...", async (job, ct) =>
        {
            try
            {
                var results = await UndoPushHandler.RunUndoPushAsync(WorkspaceId, reposSnapshot, keepChanges, job.ReportProgress, ct);

                SafeInvoke(() =>
                {
                    foreach (var (repoId, success, errMsg) in results)
                    {
                        if (success)
                            repositoryErrors.Remove(repoId);
                        else if (errMsg != null)
                        {
                            repositoryErrors[repoId] = errMsg;
                            var repoName = reposSnapshot.FirstOrDefault(w => w.RepositoryId == repoId)?.Repository?.RepositoryName ?? repoId.ToString();
                            ToastService.Show($"{repoName}: {errMsg}");
                        }
                    }
                });

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error resetting outgoing commits for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = "An error occurred while resetting commits. The GrayMoon Agent may be offline.");
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private void CloseUndoPushModal()
    {
        _undoPushModal = _undoPushModal with { IsVisible = false };
        StateHasChanged();
    }

    private Task RestorePackagesAsync()
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        JobService.StartJob(PageJobKey, "Restoring packages...", async (job, ct) =>
            await RestorePackagesCoreAsync(job, ct));

        return Task.CompletedTask;
    }

    private async Task RestorePackagesCoreAsync(BackgroundJobHandle job, CancellationToken ct)
    {
        job.ReportProgress("Restoring packages...");
        try
        {
            int count;
            await using (var scope = ServiceScopeFactory.CreateAsyncScope())
            {
                var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                count = await workspaceGitService.RestoreAllWorkspacePackagesAsync(
                    WorkspaceId,
                    job.ReportProgress,
                    ct);
            }

            if (count > 0)
                SafeInvoke(() => ToastService.Show($"Restored packages in {count} {(count == 1 ? "project" : "projects")}"));
        }
        catch (OperationCanceledException)
        {
            SafeInvoke(() => ToastService.Show("Restore cancelled."));
            throw;
        }
        catch (AgentNotConnectedException ex)
        {
            Logger.LogError(ex, "Restore packages failed (agent not connected) for workspace {WorkspaceId}", WorkspaceId);
            SafeInvoke(() => ToastService.ShowError($"Restore failed. {ex.Message}"));
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Restore packages failed for workspace {WorkspaceId}", WorkspaceId);
            SafeInvoke(() => ToastService.ShowError($"Restore failed: {ex.Message}"));
            throw;
        }
    }
}

