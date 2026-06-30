using GrayMoon.App.Models;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private void OnSearchChanged(ChangeEventArgs e)
    {
        searchTerm = e.Value?.ToString() ?? string.Empty;
        UpdateFilteredRepositories();
        StateHasChanged();
    }

    private void OnSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            searchTerm = string.Empty;
            UpdateFilteredRepositories();
            StateHasChanged();
        }
    }

    private void ClearSearchFilter()
    {
        searchTerm = string.Empty;
        UpdateFilteredRepositories();
        StateHasChanged();
    }

    private List<WorkspaceRepositoryLink> GetFilteredWorkspaceRepositories()
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return workspaceRepositories;
        }

        return workspaceRepositories
            .Where(wr => WorkspaceRepositoryLinkSearchMatcher.Matches(wr, searchTerm, repoSyncStatus))
            .ToList();
    }
}
