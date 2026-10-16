using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Jobs;

/// <summary>
/// One in-flight workspace mutation owned by <see cref="WorkspaceOperationRunner"/>.
/// Circuit job handles attach to this instance for overlay, progress, and abort.
/// </summary>
public sealed class WorkspaceOperation : IWorkspaceLockedOperation
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _displayMessage;
    private BackgroundJobState _state = BackgroundJobState.Running;
    private Exception? _fault;
    private CancellationTokenSource? _backgroundCts;

    public Guid Id { get; } = Guid.NewGuid();
    public int WorkspaceId { get; }
    public WorkspaceFeatureContextId? ContextId { get; }
    public bool IsStructural { get; }
    public string OperationKind { get; }
    public string OverlayKey { get; }
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

    internal WorkspaceOperation(
        int workspaceId,
        string operationKind,
        string overlayKey,
        string displayMessage,
        WorkspaceFeatureContextId? contextId = null,
        bool isStructural = true)
    {
        WorkspaceId = workspaceId;
        OperationKind = operationKind;
        OverlayKey = overlayKey;
        _displayMessage = displayMessage;
        ContextId = contextId;
        IsStructural = isStructural;
    }

    public void ReportProgress(string message)
    {
        _displayMessage = message;
        Changed?.Invoke();
    }

    /// <summary>True while a <see cref="RunBackgroundableAsync"/> phase runs: the overlay shows Background instead of Abort.</summary>
    public bool CanRunInBackground => Volatile.Read(ref _backgroundCts) is not null && _state == BackgroundJobState.Running;

    /// <inheritdoc />
    /// <remarks>
    /// One way only for now: once in the background, the work is out of sight. At some point background jobs may be
    /// listed somewhere the user can come back to and watch again (e.g. the CodeGraph build's terminal), but not yet.
    /// </remarks>
    public async Task RunBackgroundableAsync(Func<CancellationToken, Task> work)
    {
        using var background = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Volatile.Write(ref _backgroundCts, background);
        Changed?.Invoke();
        try
        {
            await work(background.Token);
        }
        catch (OperationCanceledException) when (background.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            // Sent to the background: the work goes on without us watching.
        }
        finally
        {
            Volatile.Write(ref _backgroundCts, null);
            Changed?.Invoke();
        }
    }

    /// <summary>Stops watching the current <see cref="RunBackgroundableAsync"/> phase; no-op outside one.</summary>
    public void RunInBackground()
    {
        try
        {
            Volatile.Read(ref _backgroundCts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The phase ended meanwhile.
        }
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
