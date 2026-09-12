using GrayMoon.Abstractions.Agent;

namespace GrayMoon.App.Services.Jobs;

/// <summary>Bounded in-memory terminal buffer owned by a single background job.</summary>
public sealed class JobTerminalBuffer
{
    private const int MaxLines = 800;
    private readonly object _lock = new();
    private readonly Queue<OverlayTerminalLine> _lines = new();

    public event Action? Changed;

    public IReadOnlyList<OverlayTerminalLine> GetSnapshot()
    {
        lock (_lock)
            return _lines.ToArray();
    }

    public void Append(AgentCommandStreamLine line)
    {
        lock (_lock)
        {
            _lines.Enqueue(new OverlayTerminalLine(line.StreamLabel, line.Kind, line.Text));
            while (_lines.Count > MaxLines)
                _lines.Dequeue();
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
