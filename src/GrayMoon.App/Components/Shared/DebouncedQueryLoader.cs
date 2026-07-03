namespace GrayMoon.App.Components.Shared;

/// <summary>Debounced search and cancellable query cycles for incremental list loading.</summary>
public sealed class DebouncedQueryLoader : IDisposable
{
    public const int DefaultDebounceMs = 400;

    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _queryCts;
    private int _generation;
    private bool _disposed;

    public int Generation => _generation;

    public CancellationToken BeginQueryCycle(out int generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = new CancellationTokenSource();
        _generation++;
        generation = _generation;
        return _queryCts.Token;
    }

    public CancellationToken GetQueryToken()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _queryCts ??= new CancellationTokenSource();
        return _queryCts.Token;
    }

    public void CancelQuery()
    {
        if (_disposed)
        {
            return;
        }

        _queryCts?.Cancel();
    }

    public async Task DebounceSearchAsync(Func<Task> action, int delayMs = DefaultDebounceMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var cts = _debounceCts;
        try
        {
            await Task.Delay(delayMs, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cts.IsCancellationRequested && !_disposed)
        {
            await action();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _queryCts?.Cancel();
        _queryCts?.Dispose();
    }
}
