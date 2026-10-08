using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using GrayMoon.Worker.Models;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Runs a lock scan in a short-lived child process (<c>graymoon-worker inspect-locks</c>), so a handle query that never returns
/// can only ever stall that child: the Worker stops waiting at the deadline, kills the child and carries on. Paths go in on
/// stdin as JSON; the result comes back on stdout after <see cref="OutputMarker"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LockScanChildProcess(ILogger<LockScanChildProcess> logger)
{
    internal const string Verb = LockScanProtocol.Verb;
    internal const string OutputMarker = LockScanProtocol.OutputMarker;

    /// <summary>Process start-up allowance on top of the scan budget (cold start, antivirus scanning the executable).</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(10);

    private readonly LaunchCommand? _launch = ResolveLaunch(Assembly.GetEntryAssembly(), Environment.ProcessPath);

    /// <summary>Tests: start a given command instead of this Worker (null = not available, scan in-process).</summary>
    internal LockScanChildProcess(ILogger<LockScanChildProcess> logger, LaunchCommand? launch)
        : this(logger)
    {
        _launch = launch;
    }

    internal sealed record LaunchCommand(string FileName, IReadOnlyList<string> Arguments);

    internal enum ChildStatus
    {
        Completed,
        TimedOut,
        Failed,
    }

    internal sealed record ChildResult(ChildStatus Status, LockScanOutcome? Outcome, string? Error);

    /// <summary>False when this process is not the Worker executable (for example under a test host); callers scan in-process.</summary>
    public bool IsAvailable => _launch is not null;

    /// <summary>
    /// How to start this Worker again with the <see cref="Verb"/> verb: the apphost directly, or <c>dotnet graymoon-worker.dll</c>.
    /// Null when the entry assembly is not the Worker.
    /// </summary>
    internal static LaunchCommand? ResolveLaunch(Assembly? entryAssembly, string? processPath)
    {
        if (entryAssembly?.GetName().Name != "graymoon-worker" || string.IsNullOrWhiteSpace(processPath))
            return null;

        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var location = entryAssembly.Location;
            return string.IsNullOrWhiteSpace(location) ? null : new LaunchCommand(processPath, [location, Verb]);
        }

        return new LaunchCommand(processPath, [Verb]);
    }

    internal async Task<ChildResult> RunAsync(
        IReadOnlyList<string> paths,
        IReadOnlyCollection<int> excludeProcessIds,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        if (_launch is null)
            return new ChildResult(ChildStatus.Failed, null, "The isolated lock scan is not available in this host.");

        var startInfo = new ProcessStartInfo(_launch.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in _launch.Arguments)
            startInfo.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return new ChildResult(ChildStatus.Failed, null, $"Could not start the lock scan: {ex.Message}");
        }

        if (process is null)
            return new ChildResult(ChildStatus.Failed, null, "Could not start the lock scan.");

        using (process)
        {
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(budget + StartupGrace);
            try
            {
                var request = new LockScanRequest
                {
                    Paths = [.. paths],
                    ExcludeProcessIds = [.. excludeProcessIds],
                    BudgetMilliseconds = (int)budget.TotalMilliseconds,
                };
                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
                process.StandardInput.Close();

                // Wait for the result line, not for the exit: a child whose scan thread is stuck may finish its output and
                // still take a while to be torn down.
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                {
                    if (!line.StartsWith(OutputMarker, StringComparison.Ordinal))
                        continue;

                    var response = JsonSerializer.Deserialize<LockScanResponse>(line[OutputMarker.Length..]);
                    var outcome = response?.ToOutcome(paths.Count);
                    return outcome is null
                        ? new ChildResult(ChildStatus.Failed, null, "The lock scan returned an unexpected result.")
                        : new ChildResult(ChildStatus.Completed, outcome, null);
                }

                var error = await ReadErrorAsync(stderr);
                return new ChildResult(ChildStatus.Failed, null, $"The lock scan ended without a result. {error}".Trim());
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Lock scan child process exceeded {TimeoutMs}ms and was stopped", (long)(budget + StartupGrace).TotalMilliseconds);
                return new ChildResult(ChildStatus.TimedOut, null, WindowsLockScanner.TimedOutDiagnostic);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ChildResult(ChildStatus.Failed, null, $"The lock scan failed: {ex.Message}");
            }
            finally
            {
                Stop(process);
            }
        }
    }

    private static async Task<string> ReadErrorAsync(Task<string> stderr)
    {
        try
        {
            var text = (await stderr.WaitAsync(TimeSpan.FromSeconds(2))).Trim();
            return text.Length <= 500 ? text : text[..500];
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.WaitForExit(500))
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }
    }
}
