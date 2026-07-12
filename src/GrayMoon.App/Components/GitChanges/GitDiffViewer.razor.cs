using GrayMoon.Common.Git;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.GitChanges;

public enum GitDiffViewMode
{
    SideBySide,
    Inline,
}

public sealed record GitDiffViewerOptions(bool WordWrap = false, bool IgnoreWhitespace = false);

/// <summary>
/// Thin Blazor wrapper around a single, kept-alive Monaco diff editor instance. Replaces its models on
/// each <see cref="SetDiffAsync"/> call rather than creating a new editor per file. First release uses
/// the built-in <c>vs-dark</c> theme unmodified, read-only, per the initial rollout requirements - theme
/// selection is encapsulated entirely in <c>GitDiffViewer.razor.js</c> so a future <c>graymoon-dark</c>
/// theme only requires changing that file.
/// </summary>
public sealed partial class GitDiffViewer : IAsyncDisposable
{
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

    [Parameter] public string OriginalPaneLabel { get; set; } = "Original";
    [Parameter] public string ModifiedPaneLabel { get; set; } = "Modified";

    private readonly string _elementId = $"git-diff-viewer-{Guid.NewGuid():N}";
    private readonly string _originalHeaderId = $"git-diff-header-original-{Guid.NewGuid():N}";
    private readonly string _headersRowId = $"git-diff-headers-{Guid.NewGuid():N}";
    private IJSObjectReference? _module;
    private bool _initialized;
    private bool _disposed;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _disposed)
        {
            return;
        }

        try
        {
            _module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/GitChanges/GitDiffViewer.razor.js");
            _initialized = await _module.InvokeAsync<bool>("init", _elementId, new
            {
                renderSideBySide = true,
                originalHeaderId = _originalHeaderId,
                headersRowId = _headersRowId,
            });
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone - nothing to initialize.
        }
        catch (InvalidOperationException)
        {
            // Circuit tearing down mid-render.
        }
    }

    public async Task SetDiffAsync(GitDiffDocument document)
    {
        if (!await EnsureReadyAsync())
        {
            return;
        }

        try
        {
            await _module!.InvokeVoidAsync(
                "setDiff",
                _elementId,
                document.OriginalContent ?? string.Empty,
                document.ModifiedContent ?? string.Empty,
                document.LanguageId ?? "plaintext");
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone.
        }
        catch (InvalidOperationException)
        {
            // Circuit tearing down mid-call.
        }
    }

    public async Task SetViewModeAsync(GitDiffViewMode mode)
    {
        if (!await EnsureReadyAsync())
        {
            return;
        }

        try
        {
            await _module!.InvokeVoidAsync("setViewMode", _elementId, mode == GitDiffViewMode.SideBySide ? "side-by-side" : "inline");
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone.
        }
        catch (InvalidOperationException)
        {
            // Circuit tearing down mid-call.
        }
    }

    public async Task SetOptionsAsync(GitDiffViewerOptions options)
    {
        if (!await EnsureReadyAsync())
        {
            return;
        }

        try
        {
            await _module!.InvokeVoidAsync("setOptions", _elementId, new { wordWrap = options.WordWrap, ignoreWhitespace = options.IgnoreWhitespace });
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone.
        }
        catch (InvalidOperationException)
        {
            // Circuit tearing down mid-call.
        }
    }

    public async Task GoToNextChangeAsync()
    {
        if (!await EnsureReadyAsync())
        {
            return;
        }

        try
        {
            await _module!.InvokeVoidAsync("goToNextChange", _elementId);
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone.
        }
        catch (InvalidOperationException)
        {
            // Circuit tearing down mid-call.
        }
    }

    public async Task GoToPreviousChangeAsync()
    {
        if (!await EnsureReadyAsync())
        {
            return;
        }

        try
        {
            await _module!.InvokeVoidAsync("goToPreviousChange", _elementId);
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone.
        }
        catch (InvalidOperationException)
        {
            // Circuit tearing down mid-call.
        }
    }

    public async Task ClearAsync()
    {
        if (!await EnsureReadyAsync())
        {
            return;
        }

        try
        {
            await _module!.InvokeVoidAsync("clear", _elementId);
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone.
        }
        catch (InvalidOperationException)
        {
            // Circuit tearing down mid-call.
        }
    }

    private async Task<bool> EnsureReadyAsync()
    {
        if (_disposed)
        {
            return false;
        }

        // The editor initializes asynchronously in OnAfterRenderAsync (module import completes, then init()
        // completes slightly later); a caller that acts anywhere in that window waits here instead of
        // silently no-oping. Must be a disjunction: _module is assigned before _initialized is set, so a
        // conjunction here would stop waiting the instant _module is non-null even though init() has not
        // finished, letting the first SetDiffAsync call after page load silently skip setting the models.
        for (var attempt = 0; (!_initialized || _module == null) && attempt < 20 && !_disposed; attempt++)
        {
            await Task.Delay(25);
        }

        return _initialized && _module != null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_module != null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose", _elementId);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already gone - nothing to clean up client-side.
            }
            catch (ObjectDisposedException)
            {
                // JS runtime already torn down.
            }
            catch (InvalidOperationException)
            {
                // Circuit tearing down mid-call.
            }
        }
    }
}
