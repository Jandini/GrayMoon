using GrayMoon.App.Components.Shared;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Queries;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
namespace GrayMoon.App.Components.Pages;
public sealed partial class WorkspaceRepositories
{
    private const double VirtualRowHeightPx = 48;
    private const double VirtualHeaderHeightPx = 40;
    private const int VirtualOverscanSlots = 24;
    private const int VirtualInitialViewportSlots = 48;
    private int _scrollGeneration;
    private readonly DebouncedQueryLoader _queryLoader = new();
    private readonly Dictionary<int, WorkspaceRepositoryLink> _linkByRepoId = new();
    private readonly Dictionary<int, WorkspaceRepositoryLink> _linkByWrlId = new();
    private readonly List<VirtualSlot> _slots = new();
    private readonly HashSet<int> _tooltipLoadInFlight = new();
    private Workspace? workspace;
    private WorkspaceRepositoryHeaderStateDto? _headerState;
    private IReadOnlyDictionary<int, PullRequestInfo?> prByRepositoryId = new Dictionary<int, PullRequestInfo?>();
    private readonly Dictionary<int, DateTime> _lastPrRefreshByRepoId = new();
    private static readonly TimeSpan PrRefreshThrottle = TimeSpan.FromSeconds(10);
    private string? errorMessage;
    private bool isInitialLoading = true;
    private bool hasLoadedOnce;
    private int? totalCount;
    private string _effectiveSearch = string.Empty;
    private int _visibleStart;
    private int _visibleEnd = -1;
    private double _topSpacerPx;
    private double _bottomSpacerPx;
    private ElementReference _tbodyRef;
    private DotNetObjectReference<WorkspaceRepositories>? _virtualScrollDotNetRef;
    private bool _virtualScrollAttached;
    private int _loadedWorkspaceId;
    private bool HasRepositories => (_headerState?.TotalCount ?? 0) > 0;
    private bool hasUnmatchedDependencies => _headerState?.HasUnmatchedDependencies ?? false;
    private bool isPushRecommended => _headerState?.IsPushRecommended ?? false;
    private int? lowestLevelNeedingWork => _headerState?.LowestLevelNeedingWork;
    private bool hasTaggedRepos => _headerState?.HasTaggedRepos ?? false;
    private bool hasIncomingCommits => _headerState?.HasIncomingCommits ?? false;
    private bool? isOutOfSync => _headerState?.IsOutOfSync;
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
    private readonly HashSet<int> _tooltipLoadedRepoIds = new();
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
    private CancellationTokenSource? _backgroundWorkCts;
    private const string TagBlockedActionMessage = "Repository is on a tag; checkout a branch first.";
    private const int TableColSpan = 4;
    private enum VirtualSlotKind
    {
        LevelHeader,
        Row,
    }
    private sealed record VirtualSlot(
        VirtualSlotKind Kind,
        int? LevelKey,
        int WorkspaceRepositoryId,
        int RepositoryId,
        int LevelRepoCount,
        int StripeIndex);
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
        CacheLink(link, WorkspaceRepositoryLinkListMapper.ToPullRequestInfo(dto));
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
    private void ClearGridState()
    {
        _slots.Clear();
        _linkByRepoId.Clear();
        _linkByWrlId.Clear();
        prByRepositoryId = new Dictionary<int, PullRequestInfo?>();
        repoSyncStatus = new();
        _visibleStart = 0;
        _visibleEnd = -1;
        _topSpacerPx = 0;
        _bottomSpacerPx = 0;
        totalCount = null;
        _headerState = null;
        _tooltipLoadedRepoIds.Clear();
        _tooltipLoadInFlight.Clear();
        _mismatchedDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<DependencyMismatchLine>>();
        _allDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<DependencyLine>>();
        _mismatchedFileVersionLinesByRepo = new Dictionary<int, IReadOnlyList<FileVersionMismatchLine>>();
        _fileLineStatusByRepo = new Dictionary<int, IReadOnlyList<WorkspaceFileLineStatus>>();
        _allFileVersionLinesByRepo = new Dictionary<int, IReadOnlyList<FileVersionDisplayLine>>();
        _customDependencyLinesByRepo = new Dictionary<int, IReadOnlyList<string>>();
    }
    private void BuildSlots(IReadOnlyList<WorkspaceRepositoryLinkIndexEntry> index)
    {
        _slots.Clear();
        if (index.Count == 0)
        {
            return;
        }
        var levelCounts = new Dictionary<int, int>();
        foreach (var entry in index)
        {
            var levelKey = entry.DependencyLevel ?? int.MinValue;
            levelCounts[levelKey] = levelCounts.GetValueOrDefault(levelKey) + 1;
        }
        var previousLevel = int.MinValue;
        var hasPrevious = false;
        var stripeIndex = 0;
        foreach (var entry in index)
        {
            var levelKey = entry.DependencyLevel ?? int.MinValue;
            if (!hasPrevious || levelKey != previousLevel)
            {
                _slots.Add(new VirtualSlot(
                    VirtualSlotKind.LevelHeader,
                    entry.DependencyLevel,
                    0,
                    0,
                    levelCounts[levelKey],
                    -1));
                previousLevel = levelKey;
                hasPrevious = true;
            }
            _slots.Add(new VirtualSlot(
                VirtualSlotKind.Row,
                entry.DependencyLevel,
                entry.WorkspaceRepositoryId,
                entry.RepositoryId,
                0,
                stripeIndex++));
        }
    }

    private static string StripeClass(int stripeIndex) => VirtualScrollUi.StripeClass(stripeIndex);
    private static double SlotHeight(VirtualSlot slot) =>
        slot.Kind == VirtualSlotKind.LevelHeader ? VirtualHeaderHeightPx : VirtualRowHeightPx;
    private double TotalScrollHeightPx()
    {
        double total = 0;
        foreach (var slot in _slots)
        {
            total += SlotHeight(slot);
        }
        return total;
    }
    private void CacheLink(WorkspaceRepositoryLink link, PullRequestInfo? prInfo)
    {
        _linkByRepoId[link.RepositoryId] = link;
        _linkByWrlId[link.WorkspaceRepositoryId] = link;
        var prDict = prByRepositoryId as Dictionary<int, PullRequestInfo?>
            ?? prByRepositoryId.ToDictionary(kv => kv.Key, kv => kv.Value);
        prDict[link.RepositoryId] = prInfo;
        prByRepositoryId = prDict;
        repoSyncStatus[link.RepositoryId] = link.SyncStatus;
    }
    private void ApplyItemsFromDtos(IReadOnlyList<WorkspaceRepositoryLinkListItemDto> dtos, bool replace)
    {
        if (replace)
        {
            _linkByRepoId.Clear();
            _linkByWrlId.Clear();
            prByRepositoryId = new Dictionary<int, PullRequestInfo?>();
            repoSyncStatus = new();
        }
        foreach (var dto in dtos)
        {
            var link = WorkspaceRepositoryLinkListMapper.ToLink(dto);
            CacheLink(link, WorkspaceRepositoryLinkListMapper.ToPullRequestInfo(dto));
        }
    }
    private void ApplySyncStateFromLoadedItems()
    {
        if (_linkByRepoId.Count == 0)
        {
            return;
        }
        _ = InvokeAsync(StateHasChanged);
    }
    private IEnumerable<WorkspaceRepositoryLink> GetHydratedLinksAtLevel(int? levelKey) =>
        _linkByRepoId.Values.Where(wr => wr.DependencyLevel == levelKey);
    private bool IsLevelActionsDisabled(int? levelKey)
    {
        var hydrated = GetHydratedLinksAtLevel(levelKey).ToList();
        if (hydrated.Count == 0)
        {
            return false;
        }
        return hydrated.All(wr => wr.IsOnTag);
    }
    /// <summary>Returns true when the visible window actually changed.</summary>
    private bool UpdateVisibleRange(int start, int end)
    {
        if (_slots.Count == 0)
        {
            var wasEmpty = _visibleEnd < 0 && _visibleStart == 0 && _topSpacerPx == 0 && _bottomSpacerPx == 0;
            _visibleStart = 0;
            _visibleEnd = -1;
            _topSpacerPx = 0;
            _bottomSpacerPx = 0;
            return !wasEmpty;
        }

        start = Math.Clamp(start, 0, _slots.Count - 1);
        end = Math.Clamp(end, start, _slots.Count - 1);
        if (start == _visibleStart && end == _visibleEnd)
        {
            return false;
        }

        _visibleStart = start;
        _visibleEnd = end;
        double top = 0;
        for (var i = 0; i < start; i++)
        {
            top += SlotHeight(_slots[i]);
        }
        double bottom = 0;
        for (var i = end + 1; i < _slots.Count; i++)
        {
            bottom += SlotHeight(_slots[i]);
        }
        _topSpacerPx = top;
        _bottomSpacerPx = bottom;
        return true;
    }
    private IReadOnlyList<VirtualSlot> VisibleSlots
    {
        get
        {
            if (_slots.Count == 0 || _visibleEnd < _visibleStart)
            {
                return Array.Empty<VirtualSlot>();
            }
            var list = new List<VirtualSlot>(_visibleEnd - _visibleStart + 1);
            for (var i = _visibleStart; i <= _visibleEnd && i < _slots.Count; i++)
            {
                list.Add(_slots[i]);
            }
            return list;
        }
    }
}
