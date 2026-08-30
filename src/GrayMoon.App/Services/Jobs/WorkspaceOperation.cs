namespace GrayMoon.App.Services.Jobs;

/// <summary>
/// One in-flight workspace mutation owned by <see cref="WorkspaceOperationRunner"/>.
/// Circuit job handles attach to this instance for overlay, progress, and abort.
/// </summary>
public sealed class WorkspaceOperation
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _displayMessage;
    private BackgroundJobState _state = BackgroundJobState.Running;
    private Exception? _fault;

    public Guid Id { get; } = Guid.NewGuid();
    public int WorkspaceId { get; }
    public string OperationKind { get; }
    public string OverlayFamily { get; }
    public JobTerminalBuffer Terminal { get; } = new();
    public CancellationToken CancellationToken => _cts.Token;
    public Task WhenCompleted => _completed.Task;

    public IProgress<OperationProgress> ToOperationProgress()
        => new Progress<OperationProgress>(p => ReportProgress(p.Message));

    public string DisplayMessage
    {
        get => _displayMessage;
        private set => _displayMessage = value;
    }

    public BackgroundJobState State => _state;
    public Exception? Fault => _fault;

    public event Action? Changed;

    internal WorkspaceOperation(int workspaceId, string operationKind, string overlayFamily, string displayMessage)
    {
        WorkspaceId = workspaceId;
        OperationKind = operationKind;
        OverlayFamily = overlayFamily;
        _displayMessage = displayMessage;
    }

    public void ReportProgress(string message)
    {
        _displayMessage = message;
        Changed?.Invoke();
    }

    public void Abort()
    {
        if (_state != BackgroundJobState.Running)
            return;

        _state = BackgroundJobState.Aborted;
        _cts.Cancel();
        Changed?.Invoke();
    }

    internal void MarkCompleted()
    {
        if (_state != BackgroundJobState.Running)
            return;
        _state = BackgroundJobState.Completed;
        Changed?.Invoke();
    }

    internal void MarkFaulted(Exception ex)
    {
        if (_state != BackgroundJobState.Running)
            return;
        _fault = ex;
        _state = BackgroundJobState.Faulted;
        Changed?.Invoke();
    }

    internal void MarkAborted()
    {
        _state = BackgroundJobState.Aborted;
        Changed?.Invoke();
    }

    internal void NotifySettled()
    {
        _completed.TrySetResult();
    }

    internal void Dispose()
    {
        NotifySettled();
        _cts.Dispose();
    }
}
