using GrayMoon.App.Components.Shared;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Queries;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using WorkspaceModel = GrayMoon.App.Models.Workspace;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceProjects : IAsyncDisposable, IDisposable
{
    [Parameter] public int WorkspaceId { get; set; }

    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

    private readonly DebouncedQueryLoader _queryLoader = new();
    private readonly VirtualTableScrollState<WorkspaceProjectListItemDto> _virtual = new();
    private ElementReference _tbodyRef;

    private WorkspaceModel? workspace;
    private string? errorMessage;
    private bool isInitialLoading = true;
    private bool hasLoadedOnce;
    private int? totalCount;
    private string searchTerm = string.Empty;
    private string _effectiveSearch = string.Empty;
    private bool _disposed;
    private int _loadedWorkspaceId;

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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queryLoader.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _virtual.DetachAsync(JSRuntime);
        await _virtual.DisposeAsync();
        Dispose();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedWorkspaceId == WorkspaceId && workspace != null)
        {
            return;
        }

        _loadedWorkspaceId = WorkspaceId;
        await LoadWorkspaceHeaderAsync();
        await ResetAndLoadFromTopAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!isInitialLoading && _virtual.Count > 0 && !_virtual.IsAttached && !_disposed)
        {
            await _virtual.AttachAsync(JSRuntime, this, _tbodyRef);
        }
    }

    private async Task LoadWorkspaceHeaderAsync()
    {
        try
        {
            isInitialLoading = true;
            errorMessage = null;
            workspace = await WorkspaceRepository.GetHeaderAsync(WorkspaceId);
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
        await _virtual.DetachAsync(JSRuntime);
        _virtual.Clear();
        totalCount = null;
        isInitialLoading = true;

        try
        {
            var filter = new WorkspaceProjectListFilter(WorkspaceId, _effectiveSearch);
            totalCount = await WorkspaceProjectListQueryService.CountAsync(filter, token);
            var ids = await WorkspaceProjectListQueryService.GetIndexAsync(filter, token);

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
                Logger.LogError(ex, "Error loading projects for workspace {WorkspaceId}", WorkspaceId);
                errorMessage = "Failed to load projects. Please try again later.";
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

    private async Task EnsureVisibleHydratedAsync(CancellationToken cancellationToken)
    {
        var missing = _virtual.GetMissingIds(_virtual.VisibleStart, _virtual.VisibleEnd);
        if (missing.Count == 0)
        {
            return;
        }

        var rows = await WorkspaceProjectListQueryService.GetByIdsAsync(missing, cancellationToken);
        if (cancellationToken.IsCancellationRequested || _disposed)
        {
            return;
        }

        _virtual.CacheItems(rows.Select(r => (r.ProjectId, r)));
    }

    [JSInvokable]
    public async Task OnVirtualScroll(double scrollTop, double clientHeight)
    {
        if (_disposed || _virtual.Count == 0)
        {
            return;
        }

        var generation = _queryLoader.Generation;
        var token = _queryLoader.GetQueryToken();
        var (start, end) = _virtual.ComputeRange(scrollTop, clientHeight);
        _virtual.UpdateVisibleRange(start, end);
        await EnsureVisibleHydratedAsync(token);
        if (generation != _queryLoader.Generation || _disposed)
        {
            return;
        }

        await InvokeAsync(StateHasChanged);
    }

    private static string GetFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "-";
        var name = path.Replace('\\', '/');
        var last = name.LastIndexOf('/');
        return last >= 0 ? name[(last + 1)..] : name;
    }
}
