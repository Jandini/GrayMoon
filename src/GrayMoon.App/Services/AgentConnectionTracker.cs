using System.Reflection;

namespace GrayMoon.App.Services;

public enum AgentConnectionState
{
    Connecting,
    Online,
    Offline,
    VersionMismatch
}

/// <summary>Tracks agent SignalR connection for the UI badge and desktop notifications.</summary>
public sealed class AgentConnectionTracker
{
    private readonly object _lock = new();
    private readonly List<string> _connectionIds = [];
    private readonly Dictionary<string, string> _agentVersions = new();
    private readonly string? _appSemVer;
    private AgentConnectionState _state = AgentConnectionState.Offline;
    private bool _selfUpdateInProgress;
    private event Action<AgentConnectionState>? _onStateChanged;

    public AgentConnectionTracker()
        : this(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
    {
    }

    internal AgentConnectionTracker(string? appSemVer)
    {
        _appSemVer = appSemVer;
    }

    public AgentConnectionState State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    /// <summary>
    /// True from the moment a SelfUpdate command is issued until the replacement agent
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

    public string? AgentSemVer
    {
        get
        {
            lock (_lock)
                return _agentVersions.Values.FirstOrDefault();
        }
    }

    public void OnStateChanged(Action<AgentConnectionState> handler)
    {
        AgentConnectionState current;
        lock (_lock)
        {
            _onStateChanged += handler;
            current = _state;
        }
        handler(current);
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

    public void OnAgentConnected(string connectionId)
    {
        RaiseIfChanged(() =>
        {
            if (!_connectionIds.Contains(connectionId))
                _connectionIds.Add(connectionId);
            return ApplyConnectionState();
        });
    }

    public void OnAgentDisconnected(string connectionId)
    {
        RaiseIfChanged(() =>
        {
            _connectionIds.Remove(connectionId);
            _agentVersions.Remove(connectionId);
            return ApplyConnectionState();
        });
    }

    public void ReportAgentSemVer(string connectionId, string agentSemVer)
    {
        RaiseIfChanged(() =>
        {
            _agentVersions[connectionId] = agentSemVer;
            return ApplyConnectionState();
        });
    }

    /// <summary>Returns the first agent connection ID for sending commands. Null if no agent connected.</summary>
    public string? GetAgentConnectionId()
    {
        lock (_lock)
            return _connectionIds.FirstOrDefault();
    }

    /// <summary>Must run while holding the instance lock. Returns true if listeners should be notified.</summary>
    private bool ApplyConnectionState()
    {
        var next = ComputeState();
        var endedUpdate = false;
        if (_selfUpdateInProgress && next == AgentConnectionState.Online)
        {
            var agentVersion = _agentVersions.Values.FirstOrDefault();
            if (agentVersion != null && !string.IsNullOrEmpty(_appSemVer) && agentVersion == _appSemVer)
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

    private AgentConnectionState ComputeState()
    {
        if (_connectionIds.Count == 0)
            return AgentConnectionState.Offline;

        var agentVersion = _agentVersions.Values.FirstOrDefault();
        if (agentVersion != null && !string.IsNullOrEmpty(_appSemVer) && agentVersion != _appSemVer)
            return AgentConnectionState.VersionMismatch;

        return AgentConnectionState.Online;
    }

    private void RaiseIfChanged(Func<bool> mutateUnderLock)
    {
        Action<AgentConnectionState>? handler;
        AgentConnectionState state;
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
