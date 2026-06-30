using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private Workspace? workspace;
    private List<WorkspaceRepositoryLink> workspaceRepositories = new();
    private IReadOnlyDictionary<int, PullRequestInfo?> prByRepositoryId = new Dictionary<int, PullRequestInfo?>();
    private readonly Dictionary<int, DateTime> _lastPrRefreshByRepoId = new();
    private static readonly TimeSpan PrRefreshThrottle = TimeSpan.FromSeconds(10);
    private string? errorMessage;
    private bool isLoading = true;
    private bool? isOutOfSync = null;
    private bool hasUnmatchedDependencies => workspaceRepositories.Any(wr => !wr.IsOnTag &&
        ((wr.UnmatchedDeps ?? 0) > 0 || (wr.OutOfDateFileRepos ?? 0) > 0));
    private bool isPushRecommended => workspaceRepositories.Any(wr => !wr.IsOnTag && ((wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false));
    private int? lowestLevelNeedingWork =>
        workspaceRepositories
            .Where(wr => !wr.IsOnTag && ((wr.UnmatchedDeps ?? 0) > 0 || (wr.OutOfDateFileRepos ?? 0) > 0))
            .Min(wr => (int?)wr.DependencyLevel);
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
    private IReadOnlyDictionary<int, IReadOnlyList<DependencyMismatchLine>> _mismatchedDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<DependencyMismatchLine>>();
    private IReadOnlyDictionary<int, IReadOnlyList<WorkspaceFileLineStatus>> _fileLineStatusByRepo = new Dictionary<int, IReadOnlyList<WorkspaceFileLineStatus>>();
    private IReadOnlyDictionary<int, IReadOnlyList<FileVersionMismatchLine>> _mismatchedFileVersionLinesByRepo = new Dictionary<int, IReadOnlyList<FileVersionMismatchLine>>();

    /// <summary>All workspace-internal package dependencies of each repository (PackageId + version as written in the .csproj). Used to populate the dependency-badge tooltip for repositories whose dependencies are up to date.</summary>
    private IReadOnlyDictionary<int, IReadOnlyList<DependencyLine>> _allDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<DependencyLine>>();
    /// <summary>Per-repo (FileName, TokenName, Version) triples derived from version-file configs and current GitVersions. Drives the OK badge file-dependency tooltip.</summary>
    private IReadOnlyDictionary<int, IReadOnlyList<FileVersionDisplayLine>> _allFileVersionLinesByRepo = new Dictionary<int, IReadOnlyList<FileVersionDisplayLine>>();
    /// <summary>User-declared custom dependency repo names per dependent repository (ordering only).</summary>
    private IReadOnlyDictionary<int, IReadOnlyList<string>> _customDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<string>>();
    private Dictionary<int, string> repositoryErrors = new(); // repositoryId -> error message
    private HashSet<string> clickedVersions = new(); // Track clicked versions to hide hover until mouse leaves
    private HashSet<int> clickedDependencyBadges = new(); // Track clicked dependency badges to hide tooltip immediately

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

    private const int TableColSpan = 4;

    /// <summary>True when the workspace repository link for <paramref name="repositoryId"/> is currently checked out at a tag.</summary>
    private bool IsRepoOnTag(int repositoryId) =>
        workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId)?.IsOnTag == true;

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
}
