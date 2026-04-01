namespace GrayMoon.App.Services;

/// <summary>Singleton service for showing toast notifications.</summary>
public sealed class ToastService : IToastService
{
    private string? _message;
    private bool _isVisible;
    private bool _isError;

    public event Action? OnShow;

    public string? Message => _message;
    public bool IsVisible => _isVisible;
    public bool IsError => _isError;

    public void Show(string message)
    {
        _message = message;
        _isVisible = true;
        _isError = false;
        OnShow?.Invoke();
    }

    public void ShowError(string message)
    {
        _message = message;
        _isVisible = true;
        _isError = true;
        OnShow?.Invoke();
    }

    public void Hide()
    {
        _isVisible = false;
        _message = null;
        _isError = false;
        OnShow?.Invoke();
    }
}
