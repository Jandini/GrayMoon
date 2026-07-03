using GrayMoon.App.Models;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private RepositoriesModalState _repositoriesModal = new();

    private async Task ShowRepositoriesModalAsync()
    {
        var ids = workspace?.Repositories.Select(r => r.RepositoryId).ToHashSet() ?? new HashSet<int>();
        _repositoriesModal = new RepositoriesModalState
        {
            IsVisible = true,
            SelectedRepositoryIds = ids,
            ErrorMessage = null,
            RefreshGeneration = _repositoriesModal.RefreshGeneration + 1,
        };
        StateHasChanged();

        try
        {
            var connectors = await WorkspacePageService.ConnectorRepository.GetAllAsync();
            _repositoriesModal.HasConnectors = connectors.Count > 0;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load connectors for Repositories modal.");
            _repositoriesModal.ErrorMessage = "Failed to load repositories. Please try again.";
            await InvokeAsync(StateHasChanged);
        }
    }

    private void CloseRepositoriesModal()
    {
        _repositoriesModal = new RepositoriesModalState();
        StateHasChanged();
    }

    private async Task SaveRepositoriesAsync()
    {
        if (!_repositoriesModal.IsVisible || _repositoriesModal.IsSaving)
            return;

        var ids = _repositoriesModal.SelectedRepositoryIds;
        if (ids.Count == 0)
        {
            _repositoriesModal.ErrorMessage = "At least one repository must be selected.";
            StateHasChanged();
            return;
        }

        _repositoriesModal.IsSaving = true;
        _repositoriesModal.ErrorMessage = null;
        StateHasChanged();

        try
        {
            await WorkspacePageService.WorkspaceRepository.UpdateAsync(
                WorkspaceId,
                workspace?.Name ?? string.Empty,
                ids,
                workspace?.RootPath);
            await ReloadWorkspaceDataAsync();
            CloseRepositoriesModal();
            ToastService.Show("Workspace repositories updated.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save repositories for workspace {WorkspaceId}", WorkspaceId);
            _repositoriesModal.IsSaving = false;
            _repositoriesModal.ErrorMessage = "Failed to save repositories. Please try again.";
            StateHasChanged();
        }
    }

    private void AbortFetchRepositories()
    {
        _fetchRepositoriesCts?.Cancel();
    }

    private async Task FetchRepositoriesAsync()
    {
        if (!_repositoriesModal.HasConnectors || _repositoriesModal.IsFetching)
            return;

        _fetchRepositoriesCts?.Cancel();
        _fetchRepositoriesCts?.Dispose();
        _fetchRepositoriesCts = new CancellationTokenSource();
        var cts = _fetchRepositoriesCts;

        _repositoriesModal.IsFetching = true;
        _repositoriesModal.FetchedRepositoryCount = null;
        _repositoriesModal.FetchError = null;
        _repositoriesModal.RenameWarnings = null;
        StateHasChanged();

        try
        {
            var progress = new Progress<int>(count =>
            {
                _repositoriesModal.FetchedRepositoryCount = count;
                _ = InvokeAsync(StateHasChanged);
            });

            var result = await WorkspacePageService.RepositoryService.RefreshRepositoriesAsync(progress, cts.Token);
            _repositoriesModal.RenameWarnings = result.RenamedRepositories.Count > 0 ? result.RenamedRepositories : null;
            _repositoriesModal.RefreshGeneration++;
        }
        catch (OperationCanceledException)
        {
            _repositoriesModal.RefreshGeneration++;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error fetching repositories for workspace {WorkspaceId}", WorkspaceId);
            _repositoriesModal.FetchError = $"Failed to fetch repositories: {ex.Message}";
        }
        finally
        {
            _repositoriesModal.IsFetching = false;
            _repositoriesModal.FetchedRepositoryCount = null;
            await InvokeAsync(StateHasChanged);
        }
    }

    private sealed class RepositoriesModalState
    {
        public bool IsVisible { get; set; }
        public HashSet<int> SelectedRepositoryIds { get; set; } = new();
        public bool HasConnectors { get; set; }
        public bool IsSaving { get; set; }
        public string? ErrorMessage { get; set; }
        public bool IsFetching { get; set; }
        public int? FetchedRepositoryCount { get; set; }
        public string? FetchError { get; set; }
        public IReadOnlyList<RenamedRepositoryInfo>? RenameWarnings { get; set; }
        public int RefreshGeneration { get; set; }
    }
}
