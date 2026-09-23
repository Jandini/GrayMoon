using System.Text.Json;
using GrayMoon.App.Components.GitChanges;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    private const string MdViewStorageKey = "graymoon.git-changes.md-view";

    private enum MdSurface
    {
        Source,
        Preview,
    }

    private MarkdownDiffViewer? _markdownViewerRef;

    private MdSurface _mdSurface = MdSurface.Preview;
    private MarkdownProsePreviewMode _mdPreviewMode = MarkdownProsePreviewMode.Changes;
    private bool _mdPreferenceLoaded;
    private string? _markdownPreviewError;

    private bool IsMarkdownFile =>
        _selectedRow is { Kind: GitChangesTreeRowKind.File, FilePath: { } path } && IsMarkdownPath(path);

    private bool ShowMarkdownPreview =>
        IsMarkdownFile
        && _mdSurface == MdSurface.Preview
        && _selectedDiff != null
        && RendersInMonaco(_selectedDiff.State);

    private bool ShowMonacoDiff =>
        _selectedDiff != null
        && RendersInMonaco(_selectedDiff.State)
        && (!IsMarkdownFile || _mdSurface == MdSurface.Source);

    private bool CanNavigateDiffChanges =>
        _selectedDiff != null
        && RendersInMonaco(_selectedDiff.State)
        && (!IsMarkdownFile
            || _mdSurface == MdSurface.Source
            || _mdPreviewMode == MarkdownProsePreviewMode.Changes);

    private bool IsMdBeforeDisabled =>
        _selectedDiff?.State is GitDiffContentState.NewFile
        || string.IsNullOrEmpty(_selectedDiff?.OriginalContent);

    private bool IsMdAfterDisabled =>
        _selectedDiff?.State is GitDiffContentState.DeletedFile
        || string.IsNullOrEmpty(_selectedDiff?.ModifiedContent);

    private string MarkdownSideLabel =>
        _selectedRow == null
            ? string.Empty
            : _selectedRow.IsStagedSection
                ? (_mdPreviewMode == MarkdownProsePreviewMode.Before ? "(HEAD)" : "(Index)")
                : (_mdPreviewMode == MarkdownProsePreviewMode.Before ? "(Index)" : "(Working Tree)");

    private static bool IsMarkdownPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureMdPreferenceLoadedAsync()
    {
        if (_mdPreferenceLoaded || _disposed)
        {
            return;
        }

        _mdPreferenceLoaded = true;

        try
        {
            var raw = await Js.InvokeAsync<string?>("graymoonStorageGet", MdViewStorageKey);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("surface", out var surface)
                && surface.GetString() is { } surfaceText
                && Enum.TryParse<MdSurface>(surfaceText, ignoreCase: true, out var parsedSurface))
            {
                _mdSurface = parsedSurface;
            }

            if (doc.RootElement.TryGetProperty("previewMode", out var mode)
                && mode.GetString() is { } modeText
                && Enum.TryParse<MarkdownProsePreviewMode>(modeText, ignoreCase: true, out var parsedMode))
            {
                _mdPreviewMode = parsedMode;
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to load markdown preview preference");
        }
    }

    private async Task PersistMdPreferenceAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(new
            {
                surface = _mdSurface.ToString().ToLowerInvariant(),
                previewMode = _mdPreviewMode.ToString().ToLowerInvariant(),
            });
            await Js.InvokeVoidAsync("graymoonStorageSet", MdViewStorageKey, json);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to persist markdown preview preference");
        }
    }

    private void CoerceMdPreviewModeForDocument()
    {
        if (_mdPreviewMode == MarkdownProsePreviewMode.Before && IsMdBeforeDisabled)
        {
            _mdPreviewMode = MarkdownProsePreviewMode.Changes;
        }
        else if (_mdPreviewMode == MarkdownProsePreviewMode.After && IsMdAfterDisabled)
        {
            _mdPreviewMode = MarkdownProsePreviewMode.Changes;
        }
    }

    private async Task SetMdSurfaceAsync(MdSurface surface)
    {
        if (_mdSurface == surface)
        {
            return;
        }

        _mdSurface = surface;
        await PersistMdPreferenceAsync();

        if (surface == MdSurface.Preview)
        {
            await PushMarkdownPreviewAsync();
        }
        else if (_selectedDiff != null && RendersInMonaco(_selectedDiff.State) && _diffViewerRef != null)
        {
            // Monaco models are already warm; force layout by re-applying if needed.
            await _diffViewerRef.SetDiffAsync(_selectedDiff);
        }

        StateHasChanged();
    }

    private async Task SetMdPreviewModeAsync(MarkdownProsePreviewMode mode)
    {
        if (mode == MarkdownProsePreviewMode.Before && IsMdBeforeDisabled)
        {
            return;
        }

        if (mode == MarkdownProsePreviewMode.After && IsMdAfterDisabled)
        {
            return;
        }

        if (_mdPreviewMode == mode)
        {
            return;
        }

        _mdPreviewMode = mode;
        await PersistMdPreferenceAsync();
        await PushMarkdownPreviewAsync();
        StateHasChanged();
    }

    private async Task PushMarkdownPreviewAsync()
    {
        _markdownPreviewError = null;

        if (!IsMarkdownFile || _selectedDiff == null || !RendersInMonaco(_selectedDiff.State))
        {
            return;
        }

        CoerceMdPreviewModeForDocument();

        var result = MarkdownProseDiffService.Render(
            _selectedDiff.OriginalContent,
            _selectedDiff.ModifiedContent,
            _mdPreviewMode);

        if (result.TooLarge)
        {
            _mdSurface = MdSurface.Source;
            _markdownPreviewError = result.ErrorMessage;
            ToastService.Show(result.ErrorMessage ?? "Markdown preview unavailable for this file.");
            await PersistMdPreferenceAsync();
            return;
        }

        if (!result.Success)
        {
            _markdownPreviewError = result.ErrorMessage ?? "Failed to render markdown preview.";
            if (_markdownViewerRef != null)
            {
                await _markdownViewerRef.ClearAsync();
            }

            return;
        }

        if (_markdownViewerRef != null)
        {
            await _markdownViewerRef.SetHtmlAsync(result.Html ?? string.Empty);
        }
    }

    private async Task NavigateNextChangeAsync()
    {
        if (!CanNavigateDiffChanges)
        {
            return;
        }

        if (IsMarkdownFile && _mdSurface == MdSurface.Preview)
        {
            if (_markdownViewerRef != null)
            {
                await _markdownViewerRef.GoToNextChangeAsync();
            }

            return;
        }

        if (_diffViewerRef != null)
        {
            await _diffViewerRef.GoToNextChangeAsync();
        }
    }

    private async Task NavigatePreviousChangeAsync()
    {
        if (!CanNavigateDiffChanges)
        {
            return;
        }

        if (IsMarkdownFile && _mdSurface == MdSurface.Preview)
        {
            if (_markdownViewerRef != null)
            {
                await _markdownViewerRef.GoToPreviousChangeAsync();
            }

            return;
        }

        if (_diffViewerRef != null)
        {
            await _diffViewerRef.GoToPreviousChangeAsync();
        }
    }

    private async Task ClearMarkdownViewerAsync()
    {
        _markdownPreviewError = null;
        if (_markdownViewerRef != null)
        {
            await _markdownViewerRef.ClearAsync();
        }
    }
}
