namespace GrayMoon.App.Services;

/// <summary>Service for showing brief toast notifications that match the app theme.</summary>
public interface IToastService
{
    /// <summary>Show a toast message. Subscribers are notified so the UI can update.</summary>
    void Show(string message);

    /// <summary>Show an error toast message with distinct error styling.</summary>
    void ShowError(string message);

    /// <summary>Fired when a new toast should be shown so the UI can re-render.</summary>
    event Action? OnShow;

    /// <summary>Current message to display, or null if no toast is visible.</summary>
    string? Message { get; }

    /// <summary>Whether a toast is currently visible.</summary>
    bool IsVisible { get; }

    /// <summary>Whether the current toast represents an error.</summary>
    bool IsError { get; }

    /// <summary>Hide the current toast.</summary>
    void Hide();
}
