using GrayMoon.Abstractions.Agent;

namespace GrayMoon.App.Services;

/// <summary>Bounded in-memory terminal buffer owned by a single background job.</summary>
public sealed class JobTerminalBuffer
{
    private const int MaxLines = 800;
    private readonly object _lock = new();
    private readonly List<OverlayTerminalLine> _lines = [];

    public event Action? Changed;

    public IReadOnlyList<OverlayTerminalLine> GetSnapshot()
    {
        lock (_lock)
            return _lines.ToList();
    }

    public void Append(AgentCommandStreamLine line)
    {
        lock (_lock)
        {
            _lines.Add(new OverlayTerminalLine(line.StreamLabel, line.Kind, line.Text));
            while (_lines.Count > MaxLines)
                _lines.RemoveAt(0);
        }

        Changed?.Invoke();
    }

    public void Append(string? streamLabel, AgentCommandStreamKind kind, string text)
    {
        Append(new AgentCommandStreamLine(streamLabel, kind, text));
    }

    public void Clear()
    {
        lock (_lock)
            _lines.Clear();

        Changed?.Invoke();
    }
}
