namespace GrayMoon.Common;

/// <summary>Async-local sink for streaming command output without threading callbacks through every caller.</summary>
public static class CommandLineStreamAmbient
{
    public static readonly AsyncLocal<Action<CommandLineStreamEvent>?> Current = new();
}

/// <summary>Pushes an ambient sink for the current async flow; restore previous on dispose.</summary>
public sealed class CommandLineStreamScope : IDisposable
{
    private readonly Action<CommandLineStreamEvent>? _prev;

    public CommandLineStreamScope(Action<CommandLineStreamEvent> sink)
    {
        _prev = CommandLineStreamAmbient.Current.Value;
        CommandLineStreamAmbient.Current.Value = sink;
    }

    public void Dispose() => CommandLineStreamAmbient.Current.Value = _prev;
}
