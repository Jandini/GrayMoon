using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
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
