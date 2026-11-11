using System.Reflection;

namespace GrayMoon.App.Services.Worker;

public enum WorkerConnectionState
{
    Connecting,
    Online,
    Offline,
    VersionMismatch
}

/// <summary>
/// Tracks worker SignalR connection for the UI badge and desktop notifications. <see cref="WorkerConnectionState.VersionMismatch"/>
/// is authoritative: <see cref="WorkerBridge"/> refuses normal commands in that state (see <see cref="WorkerVersionPolicy"/>).
/// </summary>
public sealed class WorkerConnectionTracker
{
    private readonly object _lock = new();
    private readonly List<string> _connectionIds = [];
    private readonly Dictionary<string, string> _workerVersions = new();
    private readonly Dictionary<string, string> _installDamage = new();
    private readonly string? _appSemVer;
    private WorkerConnectionState _state = WorkerConnectionState.Offline;
    private bool _selfUpdateInProgress;
    private bool _logonPasswordRequired;
    private bool _offlineAfterLogonFailure;
    private bool _installFailed;
    private int _selfUpdateGeneration;
    private readonly TimeSpan _selfUpdateTimeout;
    private readonly TimeProvider _time;
    private ITimer? _selfUpdateWatchdog;
    private event Action<WorkerConnectionState>? _onStateChanged;

    public WorkerConnectionTracker()
        : this(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
    {
    }

    /// <summary>An update that neither finishes nor reports a failure within this time is treated as failed.</summary>
    public static readonly TimeSpan DefaultSelfUpdateTimeout = TimeSpan.FromMinutes(15);

    internal WorkerConnectionTracker(string? appSemVer, TimeSpan? selfUpdateTimeout = null, TimeProvider? timeProvider = null)
    {
        _appSemVer = appSemVer;
        _selfUpdateTimeout = selfUpdateTimeout ?? DefaultSelfUpdateTimeout;
        _time = timeProvider ?? TimeProvider.System;
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

    /// <summary>
    /// True after an unattended upgrade could not start the service because the stored Windows
    /// password is stale. Cleared once the worker connects again after that failure.
    /// </summary>
    public bool LogonPasswordRequired
    {
        get
        {
            lock (_lock)
                return _logonPasswordRequired;
        }
    }

    /// <summary>
    /// True after a Worker update ended without a working Worker: the install script reported a failure,
    /// or the update timed out. Cleared when a new update starts or a matching Worker connects.
    /// </summary>
    public bool InstallFailed
    {
        get
        {
            lock (_lock)
                return _installFailed;
        }
    }

    /// <summary>
    /// What the active Worker connection reported as damaged in its install (a native library that would not load), or null.
    /// While set, <see cref="WorkerBridge"/> refuses every command except the update and diagnostics, as for a version mismatch.
    /// </summary>
    public string? InstallDamage
    {
        get
        {
            lock (_lock)
                return _connectionIds.Count == 0 ? null : _installDamage.GetValueOrDefault(_connectionIds[^1]);
        }
    }

    public void ReportInstallDamage(string connectionId, string reason)
    {
        RaiseIfChanged(() =>
        {
            _installDamage.TryGetValue(connectionId, out var previous);
            _installDamage[connectionId] = reason;
            return previous != reason;
        });
    }

    /// <summary>This App's informational version, compared with the Worker's by <see cref="WorkerVersionPolicy"/>.</summary>
    public string? AppSemVer => _appSemVer;

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

    /// <summary>Number of registered state handlers. A component that subscribes must remove its handler in Dispose.</summary>
    internal int StateChangedHandlerCount
    {
        get
        {
            lock (_lock)
                return _onStateChanged?.GetInvocationList().Length ?? 0;
        }
    }

    public void BeginSelfUpdate()
    {
        RaiseIfChanged(() =>
        {
            if (_selfUpdateInProgress)
                return false;
            _selfUpdateInProgress = true;
            _installFailed = false;
            StartSelfUpdateWatchdog(++_selfUpdateGeneration);
            return true;
        });
    }

    /// <summary>
    /// Records that the in-progress update failed (the install script reported it, or it timed out).
    /// Ends the update so the badge leaves "installing" and the failure is shown. Ignored unless an
    /// update is actually in progress.
    /// </summary>
    public void ReportInstallFailure() => RaiseIfChanged(FailSelfUpdateLocked);

    private bool FailSelfUpdateLocked()
    {
        if (!_selfUpdateInProgress)
            return false;
        _selfUpdateInProgress = false;
        _installFailed = true;
        StopSelfUpdateWatchdogLocked();
        return true;
    }

    /// <summary>
    /// A script that dies without reporting (killed, machine sleep) would leave "installing" set forever,
    /// with no notification. Fails the update if it is still the same one after the timeout.
    /// </summary>
    private void StartSelfUpdateWatchdog(int generation)
    {
        // Must run while holding the instance lock. The timer is armed here, not on a pool thread, so the
        // deadline is fixed at the moment the update begins.
        StopSelfUpdateWatchdogLocked();
        _selfUpdateWatchdog = _time.CreateTimer(
            _ => RaiseIfChanged(() => _selfUpdateGeneration == generation && FailSelfUpdateLocked()),
            null,
            _selfUpdateTimeout,
            Timeout.InfiniteTimeSpan);
    }

    private void StopSelfUpdateWatchdogLocked()
    {
        _selfUpdateWatchdog?.Dispose();
        _selfUpdateWatchdog = null;
    }

    public void EndSelfUpdate()
    {
        RaiseIfChanged(() =>
        {
            if (!_selfUpdateInProgress)
                return false;
            _selfUpdateInProgress = false;
            StopSelfUpdateWatchdogLocked();
            return true;
        });
    }

    /// <summary>
    /// Records that the in-progress unattended upgrade failed because the service logon password
    /// is no longer valid. Ends the update so the badge leaves "installing". Ignored unless an
    /// update is actually in progress.
    /// </summary>
    public void ReportLogonPasswordFailure()
    {
        RaiseIfChanged(() =>
        {
            if (!_selfUpdateInProgress)
                return false;
            _logonPasswordRequired = true;
            _selfUpdateInProgress = false;
            StopSelfUpdateWatchdogLocked();
            _offlineAfterLogonFailure = _connectionIds.Count == 0;
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
            _installDamage.Remove(connectionId);
            return ApplyConnectionState();
        });
    }

    public void ReportWorkerSemVer(string connectionId, string workerSemVer)
    {
        RaiseIfChanged(() =>
        {
            _workerVersions.TryGetValue(connectionId, out var previous);
            _workerVersions[connectionId] = workerSemVer;
            if (ApplyConnectionState())
                return true;

            // OnWorkerConnected reports Online before this version arrives, and a matching
            // version does not change that state. Listeners still need the report: the
            // startup upgrade dialog treats Online without a confirmed match as undecided.
            return _state == WorkerConnectionState.Online
                && !WorkerVersionPolicy.AreSameProductVersion(previous, _appSemVer)
                && WorkerVersionPolicy.AreSameProductVersion(workerSemVer, _appSemVer);
        });
    }

    /// <summary>
    /// Returns the active worker connection ID for sending commands: the connection that connected last.
    /// When a restarted Worker connects before its old connection has closed, new commands go to the new
    /// connection, not to the one about to disappear. Null if no worker connected.
    /// </summary>
    public string? GetWorkerConnectionId()
    {
        lock (_lock)
            return _connectionIds.LastOrDefault();
    }

    /// <summary>True while any worker connection is open.</summary>
    public bool HasWorkerConnection
    {
        get
        {
            lock (_lock)
                return _connectionIds.Count > 0;
        }
    }

    /// <summary>Must run while holding the instance lock. Returns true if listeners should be notified.</summary>
    private bool ApplyConnectionState()
    {
        if (_logonPasswordRequired)
        {
            if (_connectionIds.Count == 0)
                _offlineAfterLogonFailure = true;
            else if (_offlineAfterLogonFailure)
            {
                _logonPasswordRequired = false;
                _offlineAfterLogonFailure = false;
            }
        }

        var next = ComputeState();
        var endedUpdate = false;
        if (_selfUpdateInProgress && next == WorkerConnectionState.Online)
        {
            var workerVersion = _workerVersions.Values.FirstOrDefault();
            if (WorkerVersionPolicy.AreSameProductVersion(workerVersion, _appSemVer))
            {
                _selfUpdateInProgress = false;
                StopSelfUpdateWatchdogLocked();
                endedUpdate = true;
            }
        }

        if (_installFailed && next == WorkerConnectionState.Online
            && WorkerVersionPolicy.AreSameProductVersion(_workerVersions.Values.FirstOrDefault(), _appSemVer))
        {
            _installFailed = false;
            endedUpdate = true;
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
        if (workerVersion != null && !string.IsNullOrEmpty(_appSemVer) && !WorkerVersionPolicy.AreSameProductVersion(workerVersion, _appSemVer))
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
