using GrayMoon.App.Components.Shared;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace GrayMoon.App.Components.Modals;

public sealed partial class WorkspaceRepositoriesModal : IAsyncDisposable
{
    private readonly DebouncedQueryLoader _queryLoader = new();
    private readonly List<RepositoryListItemDto> _items = new();

    private FilterSearchInput? filterSearchInput;

    private string repositorySearch = string.Empty;
    private string _effectiveSearch = string.Empty;
    private bool _wasVisible;
    private bool _needsFocus;
    private bool _showOnlySelected;
    private HashSet<int> _selectedOnlySnapshot = new();
    private bool isInitialLoading;
    private bool isLoadingMore;
    private bool hasMore;
    private bool hasLoadedOnce;
    private int? totalCount;
    private RepositoryListCursor? _nextCursor;
    private bool _loadingNextChunk;
    private bool _disposed;
    private int _lastRefreshGeneration = -1;

    private HashSet<int> _matchingFilterIds = new();

    [Parameter] public bool IsVisible { get; set; }
    [Parameter] public string Title { get; set; } = "Select repositories";
    [Parameter] public HashSet<int> SelectedRepositoryIds { get; set; } = new();
    [Parameter] public bool HasConnectors { get; set; }
    [Parameter] public bool IsSaving { get; set; }
    [Parameter] public bool IsFetching { get; set; }
    [Parameter] public string? ErrorMessage { get; set; }
    [Parameter] public IReadOnlyList<RenamedRepositoryInfo>? RenameWarnings { get; set; }
    [Parameter] public EventCallback OnSave { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }
    [Parameter] public EventCallback OnFetchRepositories { get; set; }
    [Parameter] public bool DefaultSelectedOnly { get; set; }
    [Parameter] public int RefreshGeneration { get; set; }

    protected override void OnParametersSet()
    {
        if (IsVisible && !_wasVisible)
        {
            _wasVisible = true;
            _needsFocus = true;
            _showOnlySelected = DefaultSelectedOnly;
            _selectedOnlySnapshot = new HashSet<int>(SelectedRepositoryIds);
            _ = ResetAndLoadFromTopAsync();
        }
        else if (!IsVisible)
        {
            _wasVisible = false;
        }
        else if (IsVisible && RefreshGeneration != _lastRefreshGeneration)
        {
            _lastRefreshGeneration = RefreshGeneration;
            _ = ResetAndLoadFromTopAsync();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_needsFocus)
        {
            _needsFocus = false;
            if (filterSearchInput != null)
            {
                await filterSearchInput.FocusAsync();
            }
        }
    }

    private async Task OnRepositorySearchChangedAsync(string value)
    {
        repositorySearch = value;
        await _queryLoader.DebounceSearchAsync(async () =>
        {
            _effectiveSearch = repositorySearch;
            await ResetAndLoadFromTopAsync();
            if (!_disposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        });
    }

    private void ToggleSelectedOnly(bool value)
    {
        _showOnlySelected = value;
        if (value)
        {
            _selectedOnlySnapshot = new HashSet<int>(SelectedRepositoryIds);
        }

        _ = ResetAndLoadFromTopAsync();
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            await OnCancel.InvokeAsync();
        }
        else if (e.Key == "Enter")
        {
            await OnSave.InvokeAsync();
        }
    }

    private async Task ToggleRepository(int repositoryId, bool isSelected)
    {
        if (isSelected)
        {
            SelectedRepositoryIds.Add(repositoryId);
        }
        else
        {
            SelectedRepositoryIds.Remove(repositoryId);
        }

        await InvokeAsync(StateHasChanged);
    }

    private bool AreAllFilteredSelected() =>
        _matchingFilterIds.Count > 0 && _matchingFilterIds.All(SelectedRepositoryIds.Contains);

    private async Task ToggleAllFiltered(bool isSelected)
    {
        var filter = BuildFilter();
        var ids = await RepositoryListQueryService.GetMatchingIdsAsync(filter);
        if (isSelected)
        {
            foreach (var id in ids)
            {
                SelectedRepositoryIds.Add(id);
            }
        }
        else
        {
            foreach (var id in ids)
            {
                SelectedRepositoryIds.Remove(id);
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    private bool IsRowSelected(int repositoryId) => SelectedRepositoryIds.Contains(repositoryId);

    private async Task ResetAndLoadFromTopAsync()
    {
        if (!IsVisible)
        {
            return;
        }

        var token = _queryLoader.BeginQueryCycle(out var generation);
        _items.Clear();
        _nextCursor = null;
        hasMore = false;
        totalCount = null;
        isInitialLoading = true;
        isLoadingMore = false;
        _loadingNextChunk = false;

        try
        {
            var filter = BuildFilter();
            totalCount = await RepositoryListQueryService.CountAsync(filter, token);
            var matchingIds = await RepositoryListQueryService.GetMatchingIdsAsync(filter, token);
            if (generation != _queryLoader.Generation || _disposed)
            {
                return;
            }

            _matchingFilterIds = matchingIds.ToHashSet();
            var page = await RepositoryListQueryService.GetPageAsync(BuildRequest(null), token);
            if (generation != _queryLoader.Generation || _disposed)
            {
                return;
            }

            _items.AddRange(page.Items);
            _nextCursor = page.NextCursor;
            hasMore = page.HasMore;
            hasLoadedOnce = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (generation == _queryLoader.Generation && !_disposed)
            {
                _items.Clear();
            }
        }
        finally
        {
            if (generation == _queryLoader.Generation && !_disposed)
            {
                isInitialLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task LoadNextChunkAsync()
    {
        if (_loadingNextChunk || isInitialLoading || !hasMore || _nextCursor is null)
        {
            return;
        }

        _loadingNextChunk = true;
        isLoadingMore = true;
        var generation = _queryLoader.Generation;
        var token = _queryLoader.GetQueryToken();

        try
        {
            var page = await RepositoryListQueryService.GetPageAsync(BuildRequest(_nextCursor), token);
            if (generation != _queryLoader.Generation || _disposed)
            {
                return;
            }

            _items.AddRange(page.Items);
            _nextCursor = page.NextCursor;
            hasMore = page.HasMore;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loadingNextChunk = false;
            isLoadingMore = false;
        }
    }

    private RepositoryListFilter BuildFilter()
    {
        IReadOnlyList<int>? restrict = _showOnlySelected
            ? _selectedOnlySnapshot.ToList()
            : null;
        return new RepositoryListFilter(_effectiveSearch, restrict);
    }

    private RepositoryListRequest BuildRequest(RepositoryListCursor? cursor) =>
        new(
            _effectiveSearch,
            _showOnlySelected ? _selectedOnlySnapshot.ToList() : null,
            RepositorySortField.Name,
            SortDescending: false,
            IRepositoryListQueryService.DefaultChunkSize,
            cursor);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _queryLoader.Dispose();
        await Task.CompletedTask;
    }
}
