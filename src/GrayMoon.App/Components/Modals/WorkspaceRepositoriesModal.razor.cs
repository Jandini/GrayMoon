using GrayMoon.App.Components.Shared;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Modals;

public sealed partial class WorkspaceRepositoriesModal : IAsyncDisposable
{
    private readonly DebouncedQueryLoader _queryLoader = new();
    private readonly VirtualTableScrollState<RepositoryListItemDto> _virtual = new();
    private ElementReference _tbodyRef;

    private FilterSearchInput? filterSearchInput;

    private string repositorySearch = string.Empty;
    private string _effectiveSearch = string.Empty;
    private bool _wasVisible;
    private bool _needsFocus;
    private bool _showOnlySelected;
    private HashSet<int> _selectedOnlySnapshot = new();
    private bool isInitialLoading;
    private bool hasLoadedOnce;
    private int? totalCount;
    private bool _disposed;
    private int _lastRefreshGeneration = -1;

    private HashSet<int> _matchingFilterIds = new();

    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

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
            if (_wasVisible)
            {
                _ = _virtual.DetachAsync(JSRuntime);
                _virtual.Clear();
            }

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

        if (IsVisible && !isInitialLoading && _virtual.Count > 0 && !_virtual.IsAttached && !_disposed)
        {
            await _virtual.AttachAsync(JSRuntime, this, _tbodyRef);
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
        await _virtual.DetachAsync(JSRuntime);
        _virtual.Clear();
        totalCount = null;
        isInitialLoading = true;

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
            _virtual.SetIndex(matchingIds);
            hasLoadedOnce = true;
            await EnsureVisibleHydratedAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (generation == _queryLoader.Generation && !_disposed)
            {
                _virtual.Clear();
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

    private async Task<bool> EnsureVisibleHydratedAsync(CancellationToken cancellationToken)
    {
        var missing = _virtual.GetMissingIds(_virtual.VisibleStart, _virtual.VisibleEnd);
        if (missing.Count == 0)
        {
            return false;
        }

        var rows = await RepositoryListQueryService.GetByIdsAsync(missing, cancellationToken);
        if (cancellationToken.IsCancellationRequested || _disposed)
        {
            return false;
        }

        _virtual.CacheItems(rows.Select(r => (r.RepositoryId, r)));
        return rows.Count > 0;
    }

    [JSInvokable]
    public async Task OnVirtualScroll(double scrollTop, double clientHeight)
    {
        if (_disposed || _virtual.Count == 0)
        {
            return;
        }

        var scrollGeneration = _virtual.BeginScrollUpdate();
        var queryGeneration = _queryLoader.Generation;
        var token = _queryLoader.GetQueryToken();
        var (start, end) = _virtual.ComputeRange(scrollTop, clientHeight);
        var rangeChanged = _virtual.UpdateVisibleRange(start, end);
        if (rangeChanged && _virtual.IsCurrentScroll(scrollGeneration) && !_disposed)
        {
            await InvokeAsync(StateHasChanged);
        }

        var itemsHydrated = await EnsureVisibleHydratedAsync(token);
        if (!_virtual.IsCurrentScroll(scrollGeneration)
            || queryGeneration != _queryLoader.Generation
            || _disposed)
        {
            return;
        }

        if (itemsHydrated)
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private RepositoryListFilter BuildFilter()
    {
        IReadOnlyList<int>? restrict = _showOnlySelected
            ? _selectedOnlySnapshot.ToList()
            : null;
        return new RepositoryListFilter(_effectiveSearch, restrict);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _virtual.DetachAsync(JSRuntime);
        await _virtual.DisposeAsync();
        _queryLoader.Dispose();
    }
}
