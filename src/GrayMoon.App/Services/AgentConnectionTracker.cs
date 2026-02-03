namespace GrayMoon.App.Services;

public enum AgentConnectionState
{
    Connecting,
    Online,
    Offline
}

/// <summary>Tracks agent SignalR connection for the UI badge.</summary>
public sealed class AgentConnectionTracker
{
    private readonly object _lock = new();
    private readonly List<string> _connectionIds = [];
    private AgentConnectionState _state = AgentConnectionState.Offline;
    private event Action<AgentConnectionState>? _onStateChanged;

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
            State = AgentConnectionState.Online;
        }
    }

    public void OnAgentDisconnected(string connectionId)
    {
        lock (_lock)
        {
            _connectionIds.Remove(connectionId);
            State = _connectionIds.Count > 0 ? AgentConnectionState.Online : AgentConnectionState.Offline;
        }
    }

    /// <summary>Returns the first agent connection ID for sending commands. Null if no agent connected.</summary>
    public string? GetAgentConnectionId()
    {
        lock (_lock)
            return _connectionIds.FirstOrDefault();
    }
}
