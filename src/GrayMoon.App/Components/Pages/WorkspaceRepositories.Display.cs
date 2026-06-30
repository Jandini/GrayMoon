using GrayMoon.App.Models;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private async Task CopyVersionToClipboard(string version)
    {
        try
        {
            await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", version);
            clickedVersions.Add(version);
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Clipboard copy failed for version {Version}", version);
        }
    }

    /// <summary>Called by WorkspaceRepositoriesRow with pre-built dependency text to copy to clipboard.</summary>
    private async Task CopyDependenciesToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", text);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Clipboard copy failed for dependencies");
        }
    }

    private void OnVersionMouseLeave(string version)
    {
        if (clickedVersions.Remove(version))
        {
            StateHasChanged();
        }
    }

    private void DismissRepositoryError(int repositoryId)
    {
        if (repositoryErrors.Remove(repositoryId))
        {
            StateHasChanged();
        }
    }

    private string? GetRepositoryError(int repositoryId) =>
        repositoryErrors.TryGetValue(repositoryId, out var msg) ? msg : null;

    private RepoSyncStatus GetRepoSyncStatus(int repositoryId) =>
        repoSyncStatus.TryGetValue(repositoryId, out var status) ? status : RepoSyncStatus.NeedsSync;
}
