namespace GrayMoon.App.Services;

/// <summary>Coarse browser-tab activity tier reported by wwwroot/js/idleActivity.js.</summary>
public enum AppActivityState
{
    /// <summary>Tab visible and the user interacted (mouse/keyboard/scroll/touch) within the idle timeout.</summary>
    Active,

    /// <summary>Tab visible but no interaction within the idle timeout.</summary>
    Idle,

    /// <summary>Tab not visible (backgrounded, minimized, or another tab focused).</summary>
    Hidden,
}

/// <summary>
/// Per-circuit (one instance per browser tab) tracker of the current <see cref="AppActivityState"/>, fed by
/// <see cref="Components.Shared.AppActivityTracker"/> via JS interop. Generic and not PR-specific - any
/// polling loop (PR badges today, potentially GHA live feed later) can inject this to scale its interval
/// down when the user is actively looking at the page and back off when idle or backgrounded.
/// </summary>
public sealed class AppActivityStateService
{
    public AppActivityState State { get; private set; } = AppActivityState.Active;

    /// <summary>Raised on every state change with the new state.</summary>
    public event Action<AppActivityState>? StateChanged;

    /// <summary>
    /// Raised only when transitioning INTO Active from Idle or Hidden - the signal a poller should use to
    /// interrupt its current (slower) delay and refresh immediately instead of waiting it out.
    /// </summary>
    public event Action? BecameActive;

    public void SetState(AppActivityState state)
    {
        if (state == State)
            return;

        var previous = State;
        State = state;
        StateChanged?.Invoke(state);
        if (state == AppActivityState.Active && previous != AppActivityState.Active)
            BecameActive?.Invoke();
    }
}
