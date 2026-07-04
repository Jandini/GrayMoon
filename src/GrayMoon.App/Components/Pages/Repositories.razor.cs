using GrayMoon.App.Components.Shared;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Queries;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Pages;

public sealed partial class Repositories : IAsyncDisposable, IDisposable
{
    private readonly DebouncedQueryLoader _queryLoader = new();
    private readonly VirtualTableScrollState<RepositoryListItemDto> _virtual = new();
    private ElementReference _tbodyRef;

    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

    private IReadOnlyList<ConnectorFetchError>? connectorErrors;
    private IReadOnlyList<RenamedRepositoryInfo>? renamedRepositories;
    private string? errorMessage;
    private string searchTerm = string.Empty;
    private string _effectiveSearch = string.Empty;
    private bool isPersisting;
    private bool isInitialLoading = true;
    private bool catalogHasAny;
    private bool hasLoadedOnce;
    private int? totalCount;
    private int? fetchedRepositoryCount;
    private CancellationTokenSource? _fetchRepositoriesCts;
    private bool _disposed;

    private bool HasSearchFilter => !string.IsNullOrWhiteSpace(_effectiveSearch);

    private void ClearSearchFilter()
    {
        searchTerm = string.Empty;
        _ = OnSearchTermChangedAsync(string.Empty);
    }

    private void OnSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            searchTerm = string.Empty;
            _ = OnSearchTermChangedAsync(string.Empty);
        }
    }

    private async Task OnSearchTermChangedAsync(string value)
    {
        searchTerm = value;
        await _queryLoader.DebounceSearchAsync(async () =>
        {
            _effectiveSearch = searchTerm;
            await ResetAndLoadFromTopAsync();
            if (!_disposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        });
    }

    private void DismissConnectorErrors()
    {
        connectorErrors = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fetchRepositoriesCts?.Cancel();
        _fetchRepositoriesCts?.Dispose();
        _queryLoader.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _virtual.DetachAsync(JSRuntime);
        await _virtual.DisposeAsync();
        Dispose();
    }

    protected override async Task OnInitializedAsync()
    {
        try
        {
            catalogHasAny = await RepositoryListQueryService.AnyAsync();
            if (!catalogHasAny)
            {
                await ReloadRepositoriesAsync();
                catalogHasAny = await RepositoryListQueryService.AnyAsync();
            }

            await ResetAndLoadFromTopAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading repositories");
            errorMessage = "Failed to load repositories. Please try again later.";
            isInitialLoading = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!isInitialLoading && _virtual.Count > 0 && !_virtual.IsAttached && !_disposed)
        {
            await _virtual.AttachAsync(JSRuntime, this, _tbodyRef);
        }
    }

    private async Task ResetAndLoadFromTopAsync()
    {
        var token = _queryLoader.BeginQueryCycle(out var generation);
        await _virtual.DetachAsync(JSRuntime);
        _virtual.Clear();
        totalCount = null;
        isInitialLoading = true;
        errorMessage = null;

        try
        {
            var filter = BuildFilter();
            totalCount = await RepositoryListQueryService.CountAsync(filter, token);
            var ids = await RepositoryListQueryService.GetMatchingIdsAsync(filter, token);
            if (generation != _queryLoader.Generation || _disposed)
            {
                return;
            }

            _virtual.SetIndex(ids);
            hasLoadedOnce = true;
            await EnsureVisibleHydratedAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _queryLoader.Generation && !_disposed)
            {
                Logger.LogError(ex, "Error loading repository list");
                errorMessage = "Failed to load repositories. Please try again later.";
                _virtual.Clear();
            }
        }
        finally
        {
            if (generation == _queryLoader.Generation && !_disposed)
            {
                isInitialLoading = false;
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

    private RepositoryListFilter BuildFilter() =>
        new(_effectiveSearch, null);

    private void AbortFetchRepositories()
    {
        _fetchRepositoriesCts?.Cancel();
    }

    private async Task ReloadRepositoriesAsync()
    {
        _fetchRepositoriesCts?.Cancel();
        _fetchRepositoriesCts?.Dispose();
        _fetchRepositoriesCts = new CancellationTokenSource();

        try
        {
            isPersisting = true;
            fetchedRepositoryCount = null;
            await InvokeAsync(StateHasChanged);
            errorMessage = null;
            connectorErrors = null;

            var progress = new Progress<int>(count =>
            {
                fetchedRepositoryCount = count;
                _ = InvokeAsync(StateHasChanged);
            });
            var result = await RepositoryService.RefreshRepositoriesAsync(progress, _fetchRepositoriesCts.Token);
            connectorErrors = result.ConnectorErrors.Count > 0 ? result.ConnectorErrors : null;
            renamedRepositories = result.RenamedRepositories.Count > 0 ? result.RenamedRepositories : null;
            catalogHasAny = await RepositoryListQueryService.AnyAsync();
            await ResetAndLoadFromTopAsync();
        }
        catch (OperationCanceledException)
        {
            catalogHasAny = await RepositoryListQueryService.AnyAsync();
            await ResetAndLoadFromTopAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error refreshing repositories");
            errorMessage = "Failed to refresh repositories. Please try again later.";
        }
        finally
        {
            isPersisting = false;
            if (!_disposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }
}
