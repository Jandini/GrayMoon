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

    private bool HasAnyCalloutErrors =>
        repositoryErrors.Count > 0 || levelErrors.Count > 0 || !string.IsNullOrWhiteSpace(errorMessage);

    private void ClearRepositoryErrors()
    {
        if (!HasAnyCalloutErrors)
            return;

        repositoryErrors.Clear();
        levelErrors.Clear();
        errorMessage = null;
        StateHasChanged();
    }

    private void ClearRepositoryError(int repositoryId) =>
        repositoryErrors.Remove(repositoryId);

    private void SetRepositoryError(int repositoryId, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        repositoryErrors.TryGetValue(repositoryId, out var existing);
        var next = AppendErrorText(existing, message);
        if (next is null || string.Equals(existing, next, StringComparison.Ordinal))
            return;
        repositoryErrors[repositoryId] = next;
    }

    private void SetLevelError(int level, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        levelErrors.TryGetValue(level, out var existing);
        var next = AppendErrorText(existing, message);
        if (next is null || string.Equals(existing, next, StringComparison.Ordinal))
            return;
        levelErrors[level] = next;
    }

    private void ApplyLevelErrors(IReadOnlyDictionary<int, string>? errors)
    {
        if (errors is not { Count: > 0 })
            return;
        foreach (var (level, err) in errors)
            SetLevelError(level, err);
    }

    private void SetPageError(string? message)
    {
        var next = AppendErrorText(errorMessage, message);
        if (next is null || string.Equals(errorMessage, next, StringComparison.Ordinal))
            return;
        errorMessage = next;
    }

    private void ApplyRepositoryErrors(IReadOnlyDictionary<int, string>? repoErrors)
    {
        if (repoErrors is not { Count: > 0 })
            return;
        foreach (var (id, err) in repoErrors)
            SetRepositoryError(id, err);
    }

    private static string? AppendErrorText(string? existing, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
            return existing;
        var trimmed = incoming.Trim();
        if (string.IsNullOrWhiteSpace(existing))
            return trimmed;
        if (existing.Contains(trimmed, StringComparison.Ordinal))
            return existing;
        return existing + "\n" + trimmed;
    }

    private string? GetRepositoryError(int repositoryId) =>
        repositoryErrors.TryGetValue(repositoryId, out var msg) ? msg : null;

    private string? GetLevelError(int? levelKey)
    {
        var key = levelKey ?? 0;
        return levelErrors.TryGetValue(key, out var msg) ? msg : null;
    }

    private RepoSyncStatus GetRepoSyncStatus(int repositoryId) =>
        repoSyncStatus.TryGetValue(repositoryId, out var status) ? status : RepoSyncStatus.NeedsSync;
}
