namespace GrayMoon.App.Services;

/// <summary>
/// Whether GrayMoon Desktop's hosted top bar (sidebar brand strip + main title row) is shown.
/// This is a singleton, in-memory mirror of the persisted setting
/// (<see cref="Repositories.AppSettingRepository.TopBarShowKey"/>) - the App's own database is the
/// single source of truth, not any file on GrayMoon.Desktop. It is loaded once at startup (see
/// Program.cs) before any Blazor circuit connects, so <see cref="IsVisible"/> is always correct
/// the instant a component reads it - no per-circuit query parameter, no async DB round trip, and
/// therefore no flash of the wrong state.
///
/// GrayMoon.Desktop's tray menu toggles this live, without a page reload, by calling
/// <c>SetTopBarVisible</c> on <c>/hubs/desktop</c> (see DesktopNotificationHub); that handler
/// persists the change and calls <see cref="SetVisible"/>, which raises <see cref="Changed"/> so
/// every already-connected circuit re-renders immediately. Live re-rendering requires
/// <c>MainLayout</c> itself to declare an interactive <c>@rendermode</c> - a Layout wrapping an
/// interactive page is otherwise rendered statically once (SSR only) and never joins the live
/// circuit, so it would never receive this event.
/// </summary>
public sealed class DesktopTopBarState
{
    public bool IsVisible { get; private set; } = true;

    public event Action<bool>? Changed;

    /// <summary>Sets the initial value loaded from the database at app startup. Raises no event.</summary>
    public void LoadSilently(bool visible) => IsVisible = visible;

    public void SetVisible(bool visible)
    {
        if (IsVisible == visible) return;
        IsVisible = visible;
        Changed?.Invoke(visible);
    }
}
