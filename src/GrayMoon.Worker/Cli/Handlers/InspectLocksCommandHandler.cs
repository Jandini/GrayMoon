using System.Text.Json;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Cli;

/// <summary>
/// Hidden <c>inspect-locks</c> verb: the isolated child process <see cref="LockScanChildProcess"/> starts. Reads a
/// <see cref="LockScanRequest"/> from stdin, runs one <see cref="WindowsLockScanner"/> pass and writes the
/// <see cref="LockScanResponse"/> to stdout after the marker. Not meant to be run by hand.
/// </summary>
internal static class InspectLocksCommandHandler
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync("inspect-locks is only supported on Windows.");
            return 2;
        }

        var input = await Console.In.ReadToEndAsync(cancellationToken);
        var request = JsonSerializer.Deserialize<LockScanRequest>(input);
        if (request?.Paths is not { Count: > 0 } paths)
        {
            await Console.Error.WriteLineAsync("inspect-locks needs a JSON request with paths on stdin.");
            return 2;
        }

        var budget = TimeSpan.FromMilliseconds(Math.Clamp(request.BudgetMilliseconds, 100, 60_000));
        var outcome = WindowsLockScanner.Scan(paths, request.ExcludeProcessIds ?? [], budget, cancellationToken);
        await Console.Out.WriteLineAsync(LockScanProtocol.OutputMarker + JsonSerializer.Serialize(LockScanResponse.From(outcome)));
        await Console.Out.FlushAsync(cancellationToken);
        return 0;
    }
}
