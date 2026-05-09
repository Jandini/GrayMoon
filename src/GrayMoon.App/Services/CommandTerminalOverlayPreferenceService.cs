namespace GrayMoon.App.Services;

/// <summary>
/// Global preference for showing the live command terminal on the loading overlay.
/// Toggle via the terminal control in the overlay top-right (same pattern as 🐇 for Matrix mode).
/// </summary>
public sealed class CommandTerminalOverlayPreferenceService
{
    private bool _isVisible;

    /// <summary>When true, the overlay shows the streaming command log (if the page allows it).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            IsVisibleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when <see cref="IsVisible"/> changes.</summary>
    public event EventHandler? IsVisibleChanged;

    /// <summary>Toggles command terminal visibility on the loading overlay.</summary>
    public void Toggle() => IsVisible = !IsVisible;
}
