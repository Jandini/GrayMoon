using System.Reflection;

namespace GrayMoon.App.Services.Worker;

public enum WorkerConnectionState
{
    Connecting,
    Online,
    Offline,
    VersionMismatch
}

/// <summary>Tracks worker SignalR connection for the UI badge and desktop notifications.</summary>
public sealed class WorkerConnectionTracker
{
    private readonly object _lock = new();
    private readonly List<string> _connectionIds = [];
    private readonly Dictionary<string, string> _workerVersions = new();
    private readonly string? _appSemVer;
    private WorkerConnectionState _state = WorkerConnectionState.Offline;
    private bool _selfUpdateInProgress;
    private event Action<WorkerConnectionState>? _onStateChanged;

    public WorkerConnectionTracker()
        : this(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
    {
    }

    internal WorkerConnectionTracker(string? appSemVer)
    {
        _appSemVer = appSemVer;
    }

    public WorkerConnectionState State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    /// <summary>
    /// True from the moment a SelfUpdate command is issued until the replacement worker
    /// reconnects with a matching version (or the update is explicitly ended). Disconnects
    /// in this window are the install, not an unexpected offline.
    /// </summary>
    public bool IsSelfUpdateInProgress
    {
        get
        {
            lock (_lock)
                return _selfUpdateInProgress;
        }
    }

    public string? WorkerSemVer
    {
        get
        {
            lock (_lock)
                return _workerVersions.Values.FirstOrDefault();
        }
    }

    public void OnStateChanged(Action<WorkerConnectionState> handler)
    {
        WorkerConnectionState current;
        lock (_lock)
        {
            _onStateChanged += handler;
            current = _state;
        }
        handler(current);
    }

    public void RemoveStateChanged(Action<WorkerConnectionState> handler)
    {
        lock (_lock)
            _onStateChanged -= handler;
    }

    public void BeginSelfUpdate()
    {
        RaiseIfChanged(() =>
        {
            if (_selfUpdateInProgress)
                return false;
            _selfUpdateInProgress = true;
            return true;
        });
    }

    public void EndSelfUpdate()
    {
        RaiseIfChanged(() =>
        {
            if (!_selfUpdateInProgress)
                return false;
            _selfUpdateInProgress = false;
            return true;
        });
    }

    public void OnWorkerConnected(string connectionId)
    {
        RaiseIfChanged(() =>
        {
            if (!_connectionIds.Contains(connectionId))
                _connectionIds.Add(connectionId);
            return ApplyConnectionState();
        });
    }

    public void OnWorkerDisconnected(string connectionId)
    {
        RaiseIfChanged(() =>
        {
            _connectionIds.Remove(connectionId);
            _workerVersions.Remove(connectionId);
            return ApplyConnectionState();
        });
    }

    public void ReportWorkerSemVer(string connectionId, string workerSemVer)
    {
        RaiseIfChanged(() =>
        {
            _workerVersions[connectionId] = workerSemVer;
            return ApplyConnectionState();
        });
    }

    /// <summary>Returns the first worker connection ID for sending commands. Null if no worker connected.</summary>
    public string? GetWorkerConnectionId()
    {
        lock (_lock)
            return _connectionIds.FirstOrDefault();
    }

    /// <summary>Must run while holding the instance lock. Returns true if listeners should be notified.</summary>
    private bool ApplyConnectionState()
    {
        var next = ComputeState();
        var endedUpdate = false;
        if (_selfUpdateInProgress && next == WorkerConnectionState.Online)
        {
            var workerVersion = _workerVersions.Values.FirstOrDefault();
            if (workerVersion != null && !string.IsNullOrEmpty(_appSemVer) && workerVersion == _appSemVer)
            {
                _selfUpdateInProgress = false;
                endedUpdate = true;
            }
        }

        if (_state == next)
            return endedUpdate;

        _state = next;
        return true;
    }

    private WorkerConnectionState ComputeState()
    {
        if (_connectionIds.Count == 0)
            return WorkerConnectionState.Offline;

        var workerVersion = _workerVersions.Values.FirstOrDefault();
        if (workerVersion != null && !string.IsNullOrEmpty(_appSemVer) && workerVersion != _appSemVer)
            return WorkerConnectionState.VersionMismatch;

        return WorkerConnectionState.Online;
    }

    private void RaiseIfChanged(Func<bool> mutateUnderLock)
    {
        Action<WorkerConnectionState>? handler;
        WorkerConnectionState state;
        lock (_lock)
        {
            if (!mutateUnderLock())
                return;
            handler = _onStateChanged;
            state = _state;
        }
        handler?.Invoke(state);
    }
}
