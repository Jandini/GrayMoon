using GrayMoon.App.Models;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private async Task CopyVersionToClipboard(string version)
    {
        if (string.IsNullOrEmpty(version))
            return;

        try
        {
            await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", version);
            ToastService.Show($"{version} copied to the clipboard");
            clickedVersions.Add(version);
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Clipboard copy failed for version {Version}", version);
            ToastService.Show("Could not copy to clipboard.");
        }
    }

    /// <summary>Called by WorkspaceRepositoriesRow with pre-built dependency text to copy to clipboard.</summary>
    private async Task CopyDependenciesToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", text);
            ToastService.Show("Dependency list copied to the clipboard");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Clipboard copy failed for dependencies");
            ToastService.Show("Could not copy to clipboard.");
        }
    }

    private void OnVersionMouseLeave(string version)
    {
        if (clickedVersions.Remove(version))
        {
            StateHasChanged();
        }
    }

    private void ClearRepositoryErrors()
    {
        if (repositoryErrors.Count == 0)
            return;

        repositoryErrors.Clear();
        StateHasChanged();
    }

    private void SetRepositoryError(int repositoryId, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        repositoryErrors[repositoryId] = message;
    }

    private void ApplyRepositoryErrors(IReadOnlyDictionary<int, string>? repoErrors)
    {
        if (repoErrors is not { Count: > 0 })
            return;
        foreach (var (id, err) in repoErrors)
            SetRepositoryError(id, err);
    }

    private string? GetRepositoryError(int repositoryId) =>
        repositoryErrors.TryGetValue(repositoryId, out var msg) ? msg : null;

    private RepoSyncStatus GetRepoSyncStatus(int repositoryId) =>
        repoSyncStatus.TryGetValue(repositoryId, out var status) ? status : RepoSyncStatus.NeedsSync;
}
