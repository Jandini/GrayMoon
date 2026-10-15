using System.Collections.Concurrent;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Collapses a burst of requests for the same repository into as few passes as possible. While a pass for a path is
/// waiting out its debounce or running, further requests for that path only record that the work has to be done once
/// more, so a pass always starts after the last request it covers and never runs concurrently with another for the
/// same path.
/// </summary>
/// <remarks>
/// The first caller owns the passes and returns when the burst has been fully served; later callers return at once.
/// </remarks>
public sealed class RepositoryPassCoalescer(TimeSpan debounce)
{
    private readonly ConcurrentDictionary<string, Gate> _gates = new(PathComparer);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task RunAsync(string repositoryPath, Func<CancellationToken, Task> pass, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(repositoryPath, _ => new Gate());
        lock (gate)
        {
            if (gate.Active)
            {
                gate.Again = true;
                return;
            }

            gate.Active = true;
        }

        try
        {
            bool again;
            do
            {
                if (debounce > TimeSpan.Zero)
                    await Task.Delay(debounce, cancellationToken);

                // Requests that arrive from here on are not covered by this pass.
                lock (gate)
                    gate.Again = false;

                await pass(cancellationToken);

                lock (gate)
                {
                    again = gate.Again;
                    if (!again)
                        gate.Active = false;
                }
            }
            while (again);
        }
        catch
        {
            lock (gate)
                gate.Active = false;
            throw;
        }
    }

    private sealed class Gate
    {
        public bool Active;
        public bool Again;
    }
}
