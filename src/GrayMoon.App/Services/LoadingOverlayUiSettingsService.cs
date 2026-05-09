namespace GrayMoon.App.Services;

public enum TerminalColorScheme
{
    Green,
    Yellow
}

/// <summary>
/// In-memory overlay appearance (backdrop transparency, terminal palette). Persisted via <see cref="Repositories.AppSettingRepository"/>; load on circuit start and when Settings saves.
/// </summary>
public sealed class LoadingOverlayUiSettingsService
{
    private bool _transparentBackdrop = true;
    private TerminalColorScheme _terminalColorScheme = TerminalColorScheme.Green;

    /// <summary>When true, the loading overlay dim layer is lighter and more of the app shows through.</summary>
    public bool TransparentBackdrop
    {
        get => _transparentBackdrop;
        set
        {
            if (_transparentBackdrop == value) return;
            _transparentBackdrop = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public TerminalColorScheme TerminalColorScheme
    {
        get => _terminalColorScheme;
        set
        {
            if (_terminalColorScheme == value) return;
            _terminalColorScheme = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Changed;

    /// <summary>Sets values without raising <see cref="Changed"/> (e.g. circuit startup before any overlay exists).</summary>
    public void LoadSilently(bool transparentBackdrop, TerminalColorScheme terminalColorScheme)
    {
        _transparentBackdrop = transparentBackdrop;
        _terminalColorScheme = terminalColorScheme;
    }
}
