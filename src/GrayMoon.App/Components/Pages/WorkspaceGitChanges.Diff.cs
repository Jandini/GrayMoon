using GrayMoon.App.Components.GitChanges;
using GrayMoon.App.Services;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    private const string DiffReviewExpandedStorageKey = "graymoon.git-changes.diff-review-expanded";

    private GitDiffViewer? _diffViewerRef;
    private GitDiffDocument? _selectedDiff;
    private bool _isDiffLoading;
    private string? _diffError;

    /// <summary>When true, the file tree column is hidden so the diff/preview can use full width.</summary>
    private bool _diffReviewExpanded;
    private bool _diffReviewExpandedLoaded;
    private DotNetObjectReference<WorkspaceGitChanges>? _diffReviewEscDotNetRef;

    // Normal/NewFile/DeletedFile all have valid Original/Modified content (one side may simply be
    // empty) and render in Monaco. Binary/TooLarge/UnsupportedEncoding/Error never send content and
    // must show a placeholder instead of attempting to diff nothing.
    private static bool RendersInMonaco(GitDiffContentState state) =>
        state is GitDiffContentState.Normal or GitDiffContentState.NewFile or GitDiffContentState.DeletedFile;

    private int _diffRequestVersion;

    private async Task EnsureDiffReviewExpandedLoadedAsync()
    {
        if (_diffReviewExpandedLoaded || _disposed)
        {
            return;
        }

        _diffReviewExpandedLoaded = true;

        try
        {
            var raw = await Js.InvokeAsync<string?>("graymoonStorageGet", DiffReviewExpandedStorageKey);
            _diffReviewExpanded = string.Equals(raw, "1", StringComparison.Ordinal)
                || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to load diff review expand preference");
        }

        await SyncDiffReviewEscListenerAsync();
    }

    private async Task ToggleDiffReviewExpandedAsync()
        => await SetDiffReviewExpandedAsync(!_diffReviewExpanded);

    private async Task SetDiffReviewExpandedAsync(bool expanded)
    {
        if (_disposed)
        {
            return;
        }

        _diffReviewExpanded = expanded;

        try
        {
            await Js.InvokeVoidAsync(
                "graymoonStorageSet",
                DiffReviewExpandedStorageKey,
                _diffReviewExpanded ? "1" : "0");
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to persist diff review expand preference");
        }

        await SyncDiffReviewEscListenerAsync();
    }

    private async Task SyncDiffReviewEscListenerAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_diffReviewExpanded)
            {
                _diffReviewEscDotNetRef ??= DotNetObjectReference.Create(this);
                await Js.InvokeVoidAsync("graymoonGitChangesBindDiffReviewEscape", _diffReviewEscDotNetRef);
            }
            else
            {
                await Js.InvokeVoidAsync("graymoonGitChangesUnbindDiffReviewEscape");
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to sync diff review Escape listener");
        }
    }

    /// <summary>Invoked from JS when Escape is pressed while the file tree is hidden.</summary>
    [JSInvokable]
    public Task CollapseDiffReviewFromEscapeAsync()
    {
        if (_disposed || !_diffReviewExpanded)
        {
            return Task.CompletedTask;
        }

        return InvokeAsync(async () =>
        {
            await SetDiffReviewExpandedAsync(false);
            StateHasChanged();
        });
    }

    internal async Task UnbindDiffReviewEscListenerAsync()
    {
        try
        {
            await Js.InvokeVoidAsync("graymoonGitChangesUnbindDiffReviewEscape");
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception)
        {
        }

        _diffReviewEscDotNetRef?.Dispose();
        _diffReviewEscDotNetRef = null;
    }

    private async Task LoadDiffAsync(GitChangesTreeRow row)
    {
        var requestVersion = ++_diffRequestVersion;

        _selectedDiff = null;
        _diffError = null;
        _markdownPreviewError = null;
        _isDiffLoading = true;
        StateHasChanged();

        try
        {
            if (_diffViewerRef != null)
            {
                await _diffViewerRef.ClearAsync();
            }

            await ClearMarkdownViewerAsync();
            await EnsureMdPreferenceLoadedAsync();
            await EnsureDiffReviewExpandedLoadedAsync();

            await using var db = await DbContextFactory.CreateDbContextAsync();
            var link = await db.WorkspaceRepositories
                .Include(l => l.Workspace)
                .Include(l => l.Repository)
                .FirstOrDefaultAsync(l => l.WorkspaceRepositoryId == row.WorkspaceRepositoryId);

            if (requestVersion != _diffRequestVersion)
            {
                return;
            }

            if (link?.Workspace == null || link.Repository == null || _selectedContextId is null)
            {
                _diffError = "Repository not found.";
                return;
            }

            var (root, folderName) = await PathResolver.GetAgentWorkspaceArgsAsync(_selectedContextId.Value);
            if (requestVersion != _diffRequestVersion)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                _diffError = "Workspace root path is not configured.";
                return;
            }

            var comparison = row.IsStagedSection ? GitDiffComparison.Staged : GitDiffComparison.Unstaged;
            GitChangesDiffResult result;
            using (TerminalSinkContext.Suppress())
            {
                // File content, not a command log - never let this leak into a background job's
                // LoadingOverlay terminal, even when LoadDiffAsync runs inside one (e.g. restoring a
                // remembered selection right after a Refresh job's reload).
                result = await AgentClient.GetDiffAsync(
                    root, folderName, link.Repository.RepositoryName, row.FilePath!, comparison, CancellationToken.None);
            }

            if (requestVersion != _diffRequestVersion)
            {
                // A newer file selection superseded this one while the fetch was in flight - discard
                // this stale response instead of clobbering the current selection's diff.
                return;
            }

            if (!result.Success || result.Diff == null)
            {
                _diffError = result.ErrorMessage ?? "Failed to load diff.";
                return;
            }

            _selectedDiff = result.Diff;
            CoerceMdPreviewModeForDocument();

            if (RendersInMonaco(_selectedDiff.State))
            {
                // Reveal the active surface before pushing content so layout/observers see non-zero size.
                _isDiffLoading = false;
                StateHasChanged();

                if (_diffViewerRef != null)
                {
                    await _diffViewerRef.SetDiffAsync(_selectedDiff);
                }

                if (IsMarkdownPath(row.FilePath!) && _mdSurface == MdSurface.Preview)
                {
                    await PushMarkdownPreviewAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load diff for {Path}", row.FilePath);
            _diffError = "Failed to load diff.";
        }
        finally
        {
            if (requestVersion == _diffRequestVersion)
            {
                _isDiffLoading = false;
                StateHasChanged();
            }
        }
    }
}
