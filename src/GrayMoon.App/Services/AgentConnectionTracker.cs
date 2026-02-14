using System.Reflection;

namespace GrayMoon.App.Services;

public enum AgentConnectionState
{
    Connecting,
    Online,
    Offline,
    VersionMismatch
}

/// <summary>Tracks agent SignalR connection for the UI badge.</summary>
public sealed class AgentConnectionTracker
{
    private readonly object _lock = new();
    private readonly List<string> _connectionIds = [];
    private readonly Dictionary<string, string> _agentVersions = new();
    private AgentConnectionState _state = AgentConnectionState.Offline;
    private string? _appSemVer;
    private event Action<AgentConnectionState>? _onStateChanged;

    public AgentConnectionTracker()
    {
        _appSemVer = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    }

    public AgentConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            _onStateChanged?.Invoke(value);
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
        lock (_lock)
        {
            _onStateChanged += handler;
            handler(State);
        }
    }

    public void OnAgentConnected(string connectionId)
    {
        lock (_lock)
        {
            if (!_connectionIds.Contains(connectionId))
                _connectionIds.Add(connectionId);
            UpdateState();
        }
    }

    public void OnAgentDisconnected(string connectionId)
    {
        lock (_lock)
        {
            _connectionIds.Remove(connectionId);
            _agentVersions.Remove(connectionId);
            UpdateState();
        }
    }

    public void ReportAgentSemVer(string connectionId, string agentSemVer)
    {
        lock (_lock)
        {
            _agentVersions[connectionId] = agentSemVer;
            UpdateState();
        }
    }

    private void UpdateState()
    {
        if (_connectionIds.Count == 0)
        {
            State = AgentConnectionState.Offline;
            return;
        }

        var agentVersion = _agentVersions.Values.FirstOrDefault();
        if (agentVersion != null && !string.IsNullOrEmpty(_appSemVer) && agentVersion != _appSemVer)
        {
            State = AgentConnectionState.VersionMismatch;
        }
        else
        {
            State = AgentConnectionState.Online;
        }
    }

    /// <summary>Returns the first agent connection ID for sending commands. Null if no agent connected.</summary>
    public string? GetAgentConnectionId()
    {
        lock (_lock)
            return _connectionIds.FirstOrDefault();
    }
}
