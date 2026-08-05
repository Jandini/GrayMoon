using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private async Task OnSearchTermChangedAsync(string value)
    {
        // FilterSearchInput already debounces before invoking this - the actual query load only runs
        // once the user pauses typing, so no additional delay is needed here.
        searchTerm = value;
        _effectiveSearch = value;
        await ResetAndLoadFromTopAsync();
        if (!_disposed)
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private void OnSearchChanged(ChangeEventArgs e)
    {
        _ = OnSearchTermChangedAsync(e.Value?.ToString() ?? string.Empty);
    }

    private void OnSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            searchTerm = string.Empty;
            _ = OnSearchTermChangedAsync(string.Empty);
        }
    }

    private void ClearSearchFilter()
    {
        searchTerm = string.Empty;
        _ = OnSearchTermChangedAsync(string.Empty);
    }
}
