using GrayMoon.App.Components.Shared;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Queries;
using Microsoft.AspNetCore.Components.Web;

namespace GrayMoon.App.Components.Pages;

public sealed partial class Repositories : IDisposable
{
    private readonly DebouncedQueryLoader _queryLoader = new();
    private readonly List<RepositoryListItemDto> _items = new();

    private IReadOnlyList<ConnectorFetchError>? connectorErrors;
    private IReadOnlyList<RenamedRepositoryInfo>? renamedRepositories;
    private string? errorMessage;
    private string searchTerm = string.Empty;
    private string _effectiveSearch = string.Empty;
    private bool isPersisting;
    private bool isInitialLoading = true;
    private bool isLoadingMore;
    private bool hasMore;
    private bool catalogHasAny;
    private bool hasLoadedOnce;
    private int? totalCount;
    private RepositoryListCursor? _nextCursor;
    private int? fetchedRepositoryCount;
    private CancellationTokenSource? _fetchRepositoriesCts;
    private bool _loadingNextChunk;
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
        _disposed = true;
        _fetchRepositoriesCts?.Cancel();
        _fetchRepositoriesCts?.Dispose();
        _queryLoader.Dispose();
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

    private async Task ResetAndLoadFromTopAsync()
    {
        var token = _queryLoader.BeginQueryCycle(out var generation);
        _items.Clear();
        _nextCursor = null;
        hasMore = false;
        totalCount = null;
        isInitialLoading = true;
        isLoadingMore = false;
        _loadingNextChunk = false;
        errorMessage = null;

        try
        {
            var filter = BuildFilter();
            totalCount = await RepositoryListQueryService.CountAsync(filter, token);
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
        catch (Exception ex)
        {
            if (generation == _queryLoader.Generation && !_disposed)
            {
                Logger.LogError(ex, "Error loading repository list");
                errorMessage = "Failed to load repositories. Please try again later.";
                _items.Clear();
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
        catch (Exception ex)
        {
            if (generation == _queryLoader.Generation && !_disposed)
            {
                Logger.LogError(ex, "Error loading more repositories");
                errorMessage = "Failed to load more repositories. Please try again later.";
            }
        }
        finally
        {
            _loadingNextChunk = false;
            isLoadingMore = false;
        }
    }

    private RepositoryListFilter BuildFilter() =>
        new(_effectiveSearch, null);

    private RepositoryListRequest BuildRequest(RepositoryListCursor? cursor) =>
        new(
            _effectiveSearch,
            null,
            RepositorySortField.Name,
            SortDescending: false,
            IRepositoryListQueryService.DefaultChunkSize,
            cursor);

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
