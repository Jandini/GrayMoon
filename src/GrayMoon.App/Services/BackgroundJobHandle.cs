namespace GrayMoon.App.Services;

public enum BackgroundJobState
{
    Running,
    Completed,
    Faulted,
    Aborted,
}

/// <summary>
/// Represents a single in-progress background job. Owned by BackgroundJobService.
/// Pages capture this to report progress; the layout overlay reads it to display status.
/// </summary>
public sealed class BackgroundJobHandle
{
    private readonly CancellationTokenSource _cts = new();

    public string JobKey { get; }
    public string DisplayMessage { get; private set; }
    public BackgroundJobState State { get; private set; } = BackgroundJobState.Running;
    public Exception? Fault { get; private set; }
    public JobTerminalBuffer Terminal { get; } = new();
    public CancellationToken CancellationToken => _cts.Token;

    public event Action? Changed;

    internal BackgroundJobHandle(string jobKey, string displayMessage)
    {
        JobKey = jobKey;
        DisplayMessage = displayMessage;
    }

    public void ReportProgress(string message)
    {
        DisplayMessage = message;
        Changed?.Invoke();
    }

    public void Abort()
    {
        _cts.Cancel();
        State = BackgroundJobState.Aborted;
        Changed?.Invoke();
    }

    internal void MarkCompleted()
    {
        State = BackgroundJobState.Completed;
        Changed?.Invoke();
    }

    internal void MarkFaulted(Exception ex)
    {
        Fault = ex;
        State = BackgroundJobState.Faulted;
        Changed?.Invoke();
    }

    internal void MarkAborted()
    {
        State = BackgroundJobState.Aborted;
        Changed?.Invoke();
    }

    internal void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
