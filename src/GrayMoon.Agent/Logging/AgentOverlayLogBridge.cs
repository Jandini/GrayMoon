using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using GrayMoon.Common;

namespace GrayMoon.Agent.Logging;

/// <summary>
/// Maps hub <c>requestId</c> to overlay stream forwarding while a command runs (Serilog may emit from threads
/// where <see cref="CommandLineStreamAmbient"/> is not set).
/// </summary>
internal static class AgentOverlayLogBridge
{
    private static readonly ConcurrentDictionary<string, Action<CommandLineStreamEvent>> Forwards = new(StringComparer.Ordinal);

    public static IDisposable Register(string requestId, Action<CommandLineStreamEvent> forward)
    {
        if (!Forwards.TryAdd(requestId, forward))
            throw new InvalidOperationException($"Duplicate overlay log bridge for request '{requestId}'.");

        return new Registration(requestId);
    }

    public static bool TryGetForward(string requestId, [NotNullWhen(true)] out Action<CommandLineStreamEvent>? forward) =>
        Forwards.TryGetValue(requestId, out forward);

    private sealed class Registration(string requestId) : IDisposable
    {
        public void Dispose() => Forwards.TryRemove(requestId, out _);
    }
}
