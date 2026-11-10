namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Scoped service for showing toast notifications: one instance per Blazor circuit (browser tab), so a toast
/// shows only in the tab that raised it. Never inject it into a singleton or hosted service; process-wide
/// work raises an event that a per-circuit component turns into a toast (see <c>Toast.razor</c>).
/// Show and Hide may be called from any thread (job bodies, timers).
/// </summary>
public sealed class ToastService : IToastService
{
    private readonly object _gate = new();
    private string? _message;
    private bool _isVisible;
    private bool _isError;
    private bool _isWarning;
    private long _shownId;

    public event Action? OnShow;

    public string? Message
    {
        get
        {
            lock (_gate)
                return _message;
        }
    }

    public bool IsVisible
    {
        get
        {
            lock (_gate)
                return _isVisible;
        }
    }

    public bool IsError
    {
        get
        {
            lock (_gate)
                return _isError;
        }
    }

    public bool IsWarning
    {
        get
        {
            lock (_gate)
                return _isWarning;
        }
    }

    public long ShownId
    {
        get
        {
            lock (_gate)
                return _shownId;
        }
    }

    public void Show(string message) => Set(message, isError: false, isWarning: false);

    public void ShowError(string message) => Set(message, isError: true, isWarning: false);

    public void ShowWarning(string message) => Set(message, isError: false, isWarning: true);

    private void Set(string message, bool isError, bool isWarning)
    {
        lock (_gate)
        {
            _message = message;
            _isVisible = true;
            _isError = isError;
            _isWarning = isWarning;
            _shownId++;
        }

        OnShow?.Invoke();
    }

    public void Hide()
    {
        lock (_gate)
        {
            _isVisible = false;
            _message = null;
            _isError = false;
            _isWarning = false;
        }

        OnShow?.Invoke();
    }
}
