namespace GrayMoon.App.Services;

/// <summary>
/// Global preference for loading overlay style. When true, overlay uses Matrix rain effect;
/// when false, uses the classic simple overlay (blue spinner). Toggle via <see cref="Toggle"/> (e.g. easter egg: click app title).
/// </summary>
public sealed class MatrixOverlayPreferenceService
{
    private bool _useMatrixEffect = true;

    /// <summary>When true, loading overlay shows Matrix rain; when false, classic overlay with blue spinner.</summary>
    public bool UseMatrixEffect
    {
        get => _useMatrixEffect;
        set
        {
            if (_useMatrixEffect == value) return;
            _useMatrixEffect = value;
            UseMatrixEffectChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when <see cref="UseMatrixEffect"/> changes. Subscribe to refresh overlay UI.</summary>
    public event EventHandler? UseMatrixEffectChanged;

    /// <summary>Toggles Matrix effect on/off. Use from easter egg (e.g. click on "Gray Moon" in nav).</summary>
    public void Toggle()
    {
        UseMatrixEffect = !UseMatrixEffect;
    }
}
