namespace GrayMoon.App.Services.Jobs;

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
/// When bound to a <see cref="WorkspaceOperation"/>, progress, terminal, and abort
/// are shared process-wide; disposing the handle does not cancel that operation.
/// </summary>
public sealed class BackgroundJobHandle
{
    private readonly CancellationTokenSource? _cts;
    private readonly WorkspaceOperation? _operation;
    private readonly JobTerminalBuffer? _ownedTerminal;
    private string _displayMessage;
    private BackgroundJobState _state = BackgroundJobState.Running;
    private Exception? _fault;

    public string JobKey { get; }
    public string DisplayMessage => _operation?.DisplayMessage ?? _displayMessage;
    public BackgroundJobState State => _operation?.State ?? _state;
    public Exception? Fault => _operation?.Fault ?? _fault;
    public JobTerminalBuffer Terminal => _operation?.Terminal ?? _ownedTerminal!;
    public CancellationToken CancellationToken => _operation?.CancellationToken ?? _cts!.Token;

    public event Action? Changed;

    internal bool IsBoundTo(WorkspaceOperation operation)
        => ReferenceEquals(_operation, operation);

    internal BackgroundJobHandle(string jobKey, string displayMessage)
    {
        JobKey = jobKey;
        _displayMessage = displayMessage;
        _cts = new CancellationTokenSource();
        _ownedTerminal = new JobTerminalBuffer();
    }

    internal BackgroundJobHandle(string jobKey, WorkspaceOperation operation)
    {
        JobKey = jobKey;
        _operation = operation;
        _displayMessage = operation.DisplayMessage;
        _operation.Changed += RaiseChanged;
    }

    public void ReportProgress(string message)
    {
        if (_operation != null)
        {
            _operation.ReportProgress(message);
            return;
        }

        _displayMessage = message;
        Changed?.Invoke();
    }

    public IProgress<OperationProgress> ToOperationProgress()
        => new Progress<OperationProgress>(p => ReportProgress(p.Message));

    public void Abort()
    {
        if (_operation != null)
        {
            _operation.Abort();
            return;
        }

        _cts!.Cancel();
        _state = BackgroundJobState.Aborted;
        Changed?.Invoke();
    }

    internal void MarkCompleted()
    {
        if (_operation != null)
        {
            _operation.MarkCompleted();
            return;
        }

        _state = BackgroundJobState.Completed;
        Changed?.Invoke();
    }

    internal void MarkFaulted(Exception ex)
    {
        if (_operation != null)
        {
            _operation.MarkFaulted(ex);
            return;
        }

        _fault = ex;
        _state = BackgroundJobState.Faulted;
        Changed?.Invoke();
    }

    internal void MarkAborted()
    {
        if (_operation != null)
        {
            _operation.MarkAborted();
            return;
        }

        _state = BackgroundJobState.Aborted;
        Changed?.Invoke();
    }

    internal void Dispose()
    {
        if (_operation != null)
        {
            _operation.Changed -= RaiseChanged;
            return;
        }

        _cts!.Cancel();
        _cts.Dispose();
    }

    private void RaiseChanged() => Changed?.Invoke();
}
