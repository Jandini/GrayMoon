using GrayMoon.App.Components.Shared;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Queries;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private readonly DebouncedQueryLoader _queryLoader = new();
    private readonly List<WorkspaceRepositoryLink> _items = new();
    private readonly Dictionary<int, WorkspaceRepositoryLink> _linkByRepoId = new();

    private Workspace? workspace;
    private WorkspaceRepositoryHeaderStateDto? _headerState;
    private IReadOnlyDictionary<int, PullRequestInfo?> prByRepositoryId = new Dictionary<int, PullRequestInfo?>();
    private readonly Dictionary<int, DateTime> _lastPrRefreshByRepoId = new();
    private static readonly TimeSpan PrRefreshThrottle = TimeSpan.FromSeconds(10);
    private string? errorMessage;
    private bool isInitialLoading = true;
    private bool isLoadingMore;
    private bool hasMore;
    private bool hasLoadedOnce;
    private int? totalCount;
    private string _effectiveSearch = string.Empty;
    private WorkspaceRepositoryLinkListCursor? _nextCursor;
    private bool _loadingNextChunk;

    private bool HasRepositories => (_headerState?.TotalCount ?? 0) > 0;
    private bool hasUnmatchedDependencies => _headerState?.HasUnmatchedDependencies ?? false;
    private bool isPushRecommended => _headerState?.IsPushRecommended ?? false;
    private int? lowestLevelNeedingWork => _headerState?.LowestLevelNeedingWork;
    private bool hasTaggedRepos => _headerState?.HasTaggedRepos ?? false;
    private bool hasIncomingCommits => _headerState?.HasIncomingCommits ?? false;
    private bool? isOutOfSync => _headerState?.IsOutOfSync;

    private IEnumerable<IGrouping<int?, WorkspaceRepositoryLink>> LoadedLevelGroups =>
        _items
            .GroupBy(wr => wr.DependencyLevel)
            .OrderByDescending(g => g.Key ?? int.MinValue);

    private bool HasSearchFilter => !string.IsNullOrWhiteSpace(_effectiveSearch);

    private bool NoRepositoriesMatchSearch =>
        hasLoadedOnce && totalCount == 0 && !isInitialLoading && !string.IsNullOrWhiteSpace(_effectiveSearch);

    private string ApiBaseUrl => NavigationManager.BaseUri.TrimEnd('/');

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
    private Dictionary<int, string> repositoryErrors = new();
    private HashSet<string> clickedVersions = new();
    private HashSet<int> clickedDependencyBadges = new();

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

    private const string TagBlockedActionMessage = "Repository is on a tag; checkout a branch first.";

    private const int TableColSpan = 4;

    private bool IsRepoOnTag(int repositoryId) =>
        TryGetLink(repositoryId)?.IsOnTag == true;

    private WorkspaceRepositoryLink? TryGetLink(int repositoryId) =>
        _linkByRepoId.TryGetValue(repositoryId, out var link) ? link : null;

    private async Task<WorkspaceRepositoryLink?> TryGetLinkAsync(int repositoryId)
    {
        if (_linkByRepoId.TryGetValue(repositoryId, out var link))
        {
            return link;
        }

        var dto = await LinkListQueryService.GetSnapshotAsync(WorkspaceId, repositoryId);
        if (dto is null)
        {
            return null;
        }

        link = WorkspaceRepositoryLinkListMapper.ToLink(dto);
        _linkByRepoId[repositoryId] = link;
        return link;
    }

    private async Task<IReadOnlyList<WorkspaceRepositoryLink>> GetAllLinksForOperationAsync()
    {
        var snapshots = await LinkListQueryService.GetAllSnapshotsAsync(WorkspaceId);
        return snapshots.Select(WorkspaceRepositoryLinkListMapper.ToLink).ToList();
    }

    private Task<IReadOnlyList<int>> GetRepositoryIdsAtLevelAsync(int? levelKey) =>
        LinkListQueryService.GetRepositoryIdsAtLevelAsync(WorkspaceId, levelKey, _effectiveSearch);

    private WorkspaceRepositoryLink? FindLink(IReadOnlyList<WorkspaceRepositoryLink> links, int repositoryId) =>
        links.FirstOrDefault(w => w.RepositoryId == repositoryId);

    private void ApplyItemsFromDtos(IReadOnlyList<WorkspaceRepositoryLinkListItemDto> dtos, bool replace)
    {
        if (replace)
        {
            _items.Clear();
            _linkByRepoId.Clear();
        }

        var prDict = replace
            ? new Dictionary<int, PullRequestInfo?>()
            : prByRepositoryId.ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var dto in dtos)
        {
            var link = WorkspaceRepositoryLinkListMapper.ToLink(dto);
            _items.Add(link);
            _linkByRepoId[link.RepositoryId] = link;
            prDict[link.RepositoryId] = WorkspaceRepositoryLinkListMapper.ToPullRequestInfo(dto);
        }

        prByRepositoryId = prDict;
    }

    private void ApplySyncStateFromLoadedItems()
    {
        if (_items.Count == 0)
        {
            return;
        }

        repoSyncStatus = _items
            .Where(wr => wr.Repository != null)
            .ToDictionary(wr => wr.RepositoryId, wr => wr.SyncStatus);
        StateHasChanged();
    }
}
