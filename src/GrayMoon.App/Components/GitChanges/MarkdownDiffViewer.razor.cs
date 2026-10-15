using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.GitChanges;

/// <summary>
/// Scrollable rendered-markdown host for Git Changes Preview. HTML is produced by
/// <see cref="Services.GitChanges.MarkdownProseDiffService"/> and applied via JS so Mermaid can
/// re-run after each content swap without fighting Blazor DOM ownership.
/// </summary>
public sealed partial class MarkdownDiffViewer : IAsyncDisposable
{
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

    [Parameter] public string? FileName { get; set; }
    [Parameter] public string SideLabel { get; set; } = string.Empty;

    private readonly string _elementId = $"markdown-diff-viewer-{Guid.NewGuid():N}";
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
            _module = await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/GitChanges/MarkdownDiffViewer.razor.js");
            _initialized = await _module.InvokeAsync<bool>("init", _elementId);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async Task SetHtmlAsync(string html)
    {
        if (!await EnsureReadyAsync())
        {
            return;
        }

        try
        {
            await _module!.InvokeVoidAsync("setHtml", _elementId, html ?? string.Empty);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
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
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Moves to the next change. False when already on the last one and <paramref name="wrap"/> is off.</summary>
    public Task<bool> GoToNextChangeAsync(bool wrap = false) => StepChangeAsync("goToNextChange", wrap);

    /// <summary>Moves to the previous change. False when already on the first one and <paramref name="wrap"/> is off.</summary>
    public Task<bool> GoToPreviousChangeAsync(bool wrap = false) => StepChangeAsync("goToPreviousChange", wrap);

    private async Task<bool> StepChangeAsync(string jsFunction, bool wrap)
    {
        if (!await EnsureReadyAsync())
        {
            return true;
        }

        try
        {
            return await _module!.InvokeAsync<bool>(jsFunction, _elementId, wrap);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return true;
    }

    private async Task<bool> EnsureReadyAsync()
    {
        if (_disposed)
        {
            return false;
        }

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
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
