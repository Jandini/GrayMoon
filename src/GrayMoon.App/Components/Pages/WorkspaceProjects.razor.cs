using GrayMoon.App.Components.Shared;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Queries;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using WorkspaceModel = GrayMoon.App.Models.Workspace;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceProjects : IDisposable
{
    [Parameter] public int WorkspaceId { get; set; }

    private readonly DebouncedQueryLoader _queryLoader = new();
    private readonly List<WorkspaceProjectListItemDto> _items = new();

    private WorkspaceModel? workspace;
    private string? errorMessage;
    private bool isInitialLoading = true;
    private bool isLoadingMore;
    private bool hasMore;
    private bool hasLoadedOnce;
    private int? totalCount;
    private string searchTerm = string.Empty;
    private string _effectiveSearch = string.Empty;
    private WorkspaceProjectListCursor? _nextCursor;
    private bool _loadingNextChunk;
    private bool _disposed;

    private bool HasSearchFilter => !string.IsNullOrWhiteSpace(_effectiveSearch);

    private bool NoProjectsMatchSearch =>
        hasLoadedOnce && totalCount == 0 && !isInitialLoading && !string.IsNullOrWhiteSpace(_effectiveSearch);

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

    public void Dispose()
    {
        _disposed = true;
        _queryLoader.Dispose();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (workspace?.WorkspaceId != WorkspaceId)
        {
            await LoadWorkspaceHeaderAsync();
            await ResetAndLoadFromTopAsync();
        }
    }

    private async Task LoadWorkspaceHeaderAsync()
    {
        try
        {
            isInitialLoading = true;
            errorMessage = null;
            workspace = await WorkspaceRepository.GetByIdAsync(WorkspaceId);
            if (workspace == null)
            {
                errorMessage = "Workspace not found.";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Failed to load projects. Please try again later.";
        }
    }

    private async Task ResetAndLoadFromTopAsync()
    {
        if (workspace == null && errorMessage == null)
        {
            return;
        }

        if (errorMessage != null)
        {
            isInitialLoading = false;
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
            var filter = new WorkspaceProjectListFilter(WorkspaceId, _effectiveSearch);
            totalCount = await WorkspaceProjectListQueryService.CountAsync(filter, token);
            var page = await WorkspaceProjectListQueryService.GetPageAsync(
                new WorkspaceProjectListRequest(
                    WorkspaceId,
                    _effectiveSearch,
                    IWorkspaceProjectListQueryService.DefaultChunkSize,
                    null),
                token);

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
                Logger.LogError(ex, "Error loading projects for workspace {WorkspaceId}", WorkspaceId);
                errorMessage = "Failed to load projects. Please try again later.";
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
            var page = await WorkspaceProjectListQueryService.GetPageAsync(
                new WorkspaceProjectListRequest(
                    WorkspaceId,
                    _effectiveSearch,
                    IWorkspaceProjectListQueryService.DefaultChunkSize,
                    _nextCursor),
                token);

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
                Logger.LogError(ex, "Error loading more projects for workspace {WorkspaceId}", WorkspaceId);
                errorMessage = "Failed to load more projects. Please try again later.";
            }
        }
        finally
        {
            _loadingNextChunk = false;
            isLoadingMore = false;
        }
    }

    private static string GetFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "-";
        var name = path.Replace('\\', '/');
        var last = name.LastIndexOf('/');
        return last >= 0 ? name[(last + 1)..] : name;
    }
}
