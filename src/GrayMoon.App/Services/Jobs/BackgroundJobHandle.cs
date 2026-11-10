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
    private readonly BackgroundJobHandle? _view;
    private readonly CancellationTokenSource? _cts;
    private readonly WorkspaceOperation? _operation;
    private readonly JobTerminalBuffer? _ownedTerminal;
    private string _displayMessage;
    private BackgroundJobState _state = BackgroundJobState.Running;
    private Exception? _fault;

    public string JobKey { get; }
    public string DisplayMessage => _view?.DisplayMessage ?? _operation?.DisplayMessage ?? _displayMessage;
    public BackgroundJobState State => _view?.State ?? _operation?.State ?? _state;
    public Exception? Fault => _view != null ? _view.Fault : _operation?.Fault ?? _fault;
    public JobTerminalBuffer Terminal => _view?.Terminal ?? _operation?.Terminal ?? _ownedTerminal!;
    public CancellationToken CancellationToken => _view?.CancellationToken ?? _operation?.CancellationToken ?? _cts!.Token;

    /// <summary>
    /// True when the work passed to the <c>StartJob</c> call that returned this handle was started. False when the
    /// start was refused (a job for the key is already running, or the Workspace or context lock is held by another
    /// operation): the work was dropped, and this handle only shows the job that is running instead. A caller
    /// must not report its own work as running or completed when this is false.
    /// </summary>
    public bool WasStarted => _view == null;

    private Action? _changed;

    public event Action? Changed
    {
        add
        {
            if (_view != null)
                _view.Changed += value;
            else
                _changed += value;
        }
        remove
        {
            if (_view != null)
                _view.Changed -= value;
            else
                _changed -= value;
        }
    }

    internal bool IsBoundTo(WorkspaceOperation operation)
        => _view == null && ReferenceEquals(_operation, operation);

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

    /// <summary>A view of <paramref name="running"/> for a caller whose own start was refused. Holds no state and no subscription.</summary>
    private BackgroundJobHandle(BackgroundJobHandle running)
    {
        JobKey = running.JobKey;
        _view = running;
        _displayMessage = running.DisplayMessage;
    }

    /// <summary>Returns a handle that shows this running job with <see cref="WasStarted"/> false.</summary>
    internal BackgroundJobHandle AsNotStarted() => new(_view ?? this);

    public void ReportProgress(string message)
    {
        // A refused start has no work of its own; it must not overwrite the running job's message.
        if (_view != null)
            return;

        if (_operation != null)
        {
            _operation.ReportProgress(message);
            return;
        }

        _displayMessage = message;
        _changed?.Invoke();
    }

    public IProgress<OperationProgress> ToOperationProgress()
        => new Progress<OperationProgress>(p => ReportProgress(p.Message));

    /// <summary>True while the bound operation runs a phase the user may send to the background.</summary>
    public bool CanRunInBackground => _view?.CanRunInBackground ?? _operation?.CanRunInBackground ?? false;

    /// <summary>Stops watching the bound operation's background-able phase; the operation then completes.</summary>
    public void RunInBackground()
    {
        if (_view != null)
            _view.RunInBackground();
        else
            _operation?.RunInBackground();
    }

    public void Abort()
    {
        if (_view != null)
        {
            _view.Abort();
            return;
        }

        if (_operation != null)
        {
            _operation.Abort();
            return;
        }

        _cts!.Cancel();
        _state = BackgroundJobState.Aborted;
        _changed?.Invoke();
    }

    internal void MarkCompleted()
    {
        if (_view != null)
            return;

        if (_operation != null)
        {
            _operation.MarkCompleted();
            return;
        }

        _state = BackgroundJobState.Completed;
        _changed?.Invoke();
    }

    internal void MarkFaulted(Exception ex)
    {
        if (_view != null)
            return;

        if (_operation != null)
        {
            _operation.MarkFaulted(ex);
            return;
        }

        _fault = ex;
        _state = BackgroundJobState.Faulted;
        _changed?.Invoke();
    }

    internal void MarkAborted()
    {
        if (_view != null)
            return;

        if (_operation != null)
        {
            _operation.MarkAborted();
            return;
        }

        _state = BackgroundJobState.Aborted;
        _changed?.Invoke();
    }

    internal void Dispose()
    {
        if (_view != null)
            return;

        if (_operation != null)
        {
            _operation.Changed -= RaiseChanged;
            return;
        }

        _cts!.Cancel();
        _cts.Dispose();
    }

    private void RaiseChanged() => _changed?.Invoke();
}
