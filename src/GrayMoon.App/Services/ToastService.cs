namespace GrayMoon.App.Services;

/// <summary>Singleton service for showing toast notifications.</summary>
public sealed class ToastService : IToastService
{
    private string? _message;
    private bool _isVisible;

    public event Action? OnShow;

    public string? Message => _message;
    public bool IsVisible => _isVisible;

    public void Show(string message)
    {
        _message = message;
        _isVisible = true;
        OnShow?.Invoke();
    }

    public void Hide()
    {
        _isVisible = false;
        _message = null;
        OnShow?.Invoke();
    }
}
