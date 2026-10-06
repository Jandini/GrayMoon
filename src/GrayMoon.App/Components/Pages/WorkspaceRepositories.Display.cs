using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.WorkspaceManifest;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    [Inject] private IDbContextFactory<AppDbContext> DbContextFactory { get; set; } = default!;
    [Inject] private IWorkerFeatureSupportService WorkerFeatureSupport { get; set; } = default!;

    /// <summary>True when the Workspace has a Workspace-role link (D13); drives the Worker compatibility banner.</summary>
    private bool _hasWorkspaceRepositoryLink;
    private bool _workerSupportsWorkspaceRepository = true;
    private bool _isResolvingManifestDrift;

    private bool ShowManifestDriftBanner => !_isFeatureContext && workspace?.ManifestDriftDetectedAt != null;

    private bool ShowWorkerCompatibilityBanner =>
        !_isFeatureContext && _hasWorkspaceRepositoryLink && !_workerSupportsWorkspaceRepository;

    /// <summary>
    /// Banner data: whether a Workspace-role link exists and, if so, whether the connected Worker advertises
    /// <see cref="WorkerFeatures.WorkspaceRepository"/> (D2). Never throws; a failed read leaves the banner hidden.
    /// </summary>
    private async Task RefreshWorkspaceRepositoryBannerStateAsync()
    {
        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();
            _hasWorkspaceRepositoryLink = await db.WorkspaceRepositories.AsNoTracking()
                .AnyAsync(l => l.WorkspaceId == WorkspaceId && l.Role == WorkspaceRepositoryRole.Workspace);
            _workerSupportsWorkspaceRepository = !_hasWorkspaceRepositoryLink
                || await WorkerFeatureSupport.SupportsAsync(WorkerFeatures.WorkspaceRepository);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "Could not read Workspace repository banner state for workspace {WorkspaceId}", WorkspaceId);
            _hasWorkspaceRepositoryLink = false;
            _workerSupportsWorkspaceRepository = true;
        }
    }

    private async Task ReloadWorkspaceHeaderRowAsync()
    {
        var refreshed = await ScopedExecutor.ExecuteAsync<WorkspaceRepository, Workspace?>(
            repo => repo.GetHeaderAsync(WorkspaceId));
        if (refreshed != null)
        {
            workspace = refreshed;
        }
    }

    /// <summary>Drift banner "Write Workspace definition to disk": rewrites .gitignore and the manifest (D5, D12), then re-checks drift (D8).</summary>
    private async Task WriteWorkspaceDefinitionToDiskAsync()
    {
        if (_isResolvingManifestDrift)
            return;

        _isResolvingManifestDrift = true;
        StateHasChanged();
        try
        {
            var error = await ManifestService.SyncDefinitionToDiskAsync(WorkspaceId);
            if (error is not null)
            {
                ToastService.ShowError(error);
                return;
            }

            await ManifestService.DetectDriftAsync(WorkspaceId);
            await ReloadWorkspaceHeaderRowAsync();
            ToastService.Show("Workspace definition written to disk.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Writing the Workspace definition failed for workspace {WorkspaceId}", WorkspaceId);
            ToastService.ShowError($"Could not write the Workspace definition: {ex.Message}");
        }
        finally
        {
            _isResolvingManifestDrift = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Drift banner "Dismiss": clears <c>Workspaces.ManifestDriftDetectedAt</c> until the next detection.</summary>
    private async Task DismissManifestDriftAsync()
    {
        if (_isResolvingManifestDrift)
            return;

        _isResolvingManifestDrift = true;
        StateHasChanged();
        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();
            await db.Workspaces
                .Where(w => w.WorkspaceId == WorkspaceId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.ManifestDriftDetectedAt, (DateTime?)null));
            await ReloadWorkspaceHeaderRowAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Dismissing the Workspace definition banner failed for workspace {WorkspaceId}", WorkspaceId);
            ToastService.ShowError($"Could not dismiss the banner: {ex.Message}");
        }
        finally
        {
            _isResolvingManifestDrift = false;
            await InvokeAsync(StateHasChanged);
        }
    }

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

    private void ClearRepositoryErrorsFor(IEnumerable<int> repositoryIds)
    {
        foreach (var repositoryId in repositoryIds)
            repositoryErrors.Remove(repositoryId);
    }

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

    /// <summary>
    /// Level errors (sync, push, update failures reported against a level or level 0) have no level header to
    /// render under in a flat grid, so they are shown as one page-level callout instead.
    /// </summary>
    private string? UngroupedLevelErrorMessage =>
        _presentation.GroupByDependencyLevel || levelErrors.Count == 0
            ? null
            : string.Join("\n", levelErrors.OrderBy(kv => kv.Key).Select(kv => kv.Value));

    private RepoSyncStatus GetRepoSyncStatus(int repositoryId) =>
        repoSyncStatus.TryGetValue(repositoryId, out var status) ? status : RepoSyncStatus.NeedsSync;
}
