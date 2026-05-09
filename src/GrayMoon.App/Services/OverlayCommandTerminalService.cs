using GrayMoon.Abstractions.Agent;

namespace GrayMoon.App.Services;

/// <summary>Bounded in-memory log for the loading-overlay terminal (agent command streams may interleave).</summary>
public sealed class OverlayCommandTerminalService
{
    private const int MaxLines = 800;
    private readonly object _lock = new();
    private readonly List<OverlayTerminalLine> _lines = [];

    public event EventHandler? Changed;

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

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_lock)
            _lines.Clear();

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record OverlayTerminalLine(string? StreamLabel, AgentCommandStreamKind Kind, string Text);
