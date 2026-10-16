using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using GrayMoon.Common;
using GrayMoon.Worker.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Runs the optional <c>codegraph</c> CLI (an npm package, so a <c>.cmd</c> shim on Windows). A folder counts as
/// initialized only when <c>.codegraph/codegraph.db</c> exists: <c>.codegraph/.gitignore</c> is committed, so the
/// folder itself is present in every worktree. Index builds run in the background on the Worker because a large
/// Workspace takes longer than the App waits for a command; removing the index stops a build still running there.
/// </summary>
public sealed class CodeGraphCli(
    ICommandLineService commandLine,
    IHostApplicationLifetime lifetime,
    ILogger<CodeGraphCli> logger)
{
    internal const string DataFolderName = ".codegraph";
    internal const string DatabaseFileName = "codegraph.db";
    internal const string LockFileName = "codegraph.lock";

    /// <summary>Files in which CodeGraph records the processes holding its index.</summary>
    internal static readonly string[] PidFileNames = ["daemon.pid", "writer.pid", "rebuild.pid", LockFileName];

    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PidFileClockSlack = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan UninitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StopInitTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, RunningInit> _inits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds the executable; tests replace it so they do not depend on what the machine has installed.</summary>
    internal Func<string?> ExecutableResolver { get; init; } = FindOnPath;

    /// <summary>Kills a process named by a pid file; tests replace it so they never touch real processes.</summary>
    internal Func<int, DateTime, CancellationToken, Task<bool>> ProcessKiller { get; init; } = KillCodeGraphProcessAsync;

    internal int DeleteAttempts { get; init; } = 10;

    internal TimeSpan DeleteRetryDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Full path of the <c>codegraph</c> executable, or null when it is not installed.</summary>
    public string? ResolveExecutable() => ExecutableResolver();

    /// <summary>Full path of the <c>codegraph</c> executable on PATH, or null when it is not on PATH.</summary>
    public static string? FindOnPath()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "codegraph.cmd", "codegraph.exe", "codegraph.bat" }
            : new[] { "codegraph" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                string path;
                try { path = Path.Combine(dir.Trim().Trim('"'), candidate); }
                catch (ArgumentException) { continue; }

                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    public static bool IsInitialized(string root) =>
        File.Exists(Path.Combine(root, DataFolderName, DatabaseFileName));

    public bool IsInitRunning(string root) => _inits.ContainsKey(Key(root));

    /// <summary>Installed version (e.g. <c>1.6.2</c>), or null when the CLI is missing or does not answer.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable();
        if (executable is null)
            return null;

        try
        {
            var result = await commandLine.RunAsync(executable, ["--version"], null, [], cancellationToken, timeout: VersionTimeout);
            if (result.ExitCode != 0)
                return null;

            var line = result.Stdout?.Trim().Split('\n', '\r').FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "codegraph --version failed");
            return null;
        }
    }

    /// <summary>
    /// Starts <c>codegraph init</c> in <paramref name="root"/> in the background. Returns false when a build is
    /// already running there. The build runs with <paramref name="root"/> as its working directory, so removing that
    /// folder stops it like any other Worker process.
    /// </summary>
    public bool TryStartInit(string executable, string root)
    {
        var key = Key(root);
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inits.TryAdd(key, new RunningInit(cancel, completion.Task)))
        {
            cancel.Dispose();
            return false;
        }

        // Not part of the command that started it: the build must not stream into that command's (finished) output.
        using var noFlow = ExecutionContext.SuppressFlow();
        _ = Task.Run(async () =>
        {
            try
            {
                logger.LogInformation("CodeGraph index build started in {Root}", root);
                var result = await commandLine.RunAsync(
                    executable, ["init", "-y", root], root, [], cancel.Token, timeout: InitTimeout);
                if (result.ExitCode == 0)
                    logger.LogInformation("CodeGraph index build finished in {Root}", root);
                else
                    logger.LogWarning(
                        "CodeGraph index build failed in {Root} (exit code {ExitCode}): {Error}",
                        root, result.ExitCode, FirstLine(result.Stderr) ?? FirstLine(result.Stdout));
            }
            catch (PathUnderRemovalException)
            {
                logger.LogInformation("CodeGraph index build stopped in {Root} because the folder is being removed", root);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("CodeGraph index build stopped in {Root}", root);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "CodeGraph index build failed in {Root}", root);
            }
            finally
            {
                _inits.TryRemove(key, out _);
                cancel.Dispose();
                completion.TrySetResult();
            }
        });

        return true;
    }

    /// <summary>Stops a background build running in <paramref name="root"/>. Returns true when one was running.</summary>
    public async Task<bool> StopInitAsync(string root, CancellationToken cancellationToken)
    {
        if (!_inits.TryGetValue(Key(root), out var running))
            return false;

        try
        {
            running.Cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The build finished in the meantime.
        }

        try
        {
            await running.Completion.WaitAsync(StopInitTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("CodeGraph index build in {Root} did not stop within {Seconds}s", root, StopInitTimeout.TotalSeconds);
        }

        return true;
    }

    /// <summary>
    /// Kills the CodeGraph processes that hold the index in <paramref name="root"/>: the shared MCP daemon an AI tool
    /// started there (detached, so it outlives the tool by its idle timeout), an MCP proxy serving the index
    /// in-process, and any writer or rebuild. <c>codegraph uninit</c> never stops them and fails while they keep the
    /// database open. Only used when the Feature root is being removed, so nothing is asked to shut down gracefully;
    /// on Windows CodeGraph's own daemon stop is a TerminateProcess too. Returns how many processes were killed.
    /// </summary>
    public async Task<int> KillIndexProcessesAsync(string root, CancellationToken cancellationToken)
    {
        var killed = 0;
        foreach (var (pid, pidFile, writtenUtc) in ReadIndexProcessIds(root))
        {
            if (!await ProcessKiller(pid, writtenUtc, cancellationToken))
                continue;

            logger.LogInformation("Killed CodeGraph process {Pid} named by {PidFile}", pid, pidFile);
            killed++;
        }

        return killed;
    }

    /// <summary>
    /// Deletes <c>.codegraph</c> in <paramref name="root"/> without CodeGraph. Between attempts it kills the index
    /// processes again: a live MCP proxy whose daemon was killed falls back to opening the index itself and respawns
    /// a daemon. Returns false when the folder is still there after the last attempt.
    /// </summary>
    public async Task<bool> DeleteIndexAsync(string root, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(root, DataFolderName);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteAttempts)
                {
                    logger.LogWarning(ex, "Could not delete CodeGraph index {Folder}", folder);
                    return false;
                }

                logger.LogDebug(ex, "CodeGraph index {Folder} is still held (attempt {Attempt})", folder, attempt);
            }

            await KillIndexProcessesAsync(root, cancellationToken);
            await Task.Delay(DeleteRetryDelay, cancellationToken);
        }
    }

    /// <summary>
    /// Process ids CodeGraph recorded in the index folder, each with its file's last write time: <c>daemon.pid</c>,
    /// <c>writer.pid</c> and <c>rebuild.pid</c> hold JSON with a <c>pid</c>, <c>codegraph.lock</c> a bare pid.
    /// </summary>
    internal static IReadOnlyList<(int Pid, string PidFile, DateTime WrittenUtc)> ReadIndexProcessIds(string root)
    {
        var found = new List<(int, string, DateTime)>();
        foreach (var name in PidFileNames)
        {
            var path = Path.Combine(root, DataFolderName, name);
            try
            {
                if (!File.Exists(path))
                    continue;

                if (ParsePid(File.ReadAllText(path)) is { } pid && found.All(f => f.Item1 != pid))
                    found.Add((pid, name, File.GetLastWriteTimeUtc(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Being rewritten or already gone; the next attempt reads it again.
            }
        }

        return found;
    }

    internal static int? ParsePid(string text)
    {
        text = text.Trim();
        if (int.TryParse(text, out var plain))
            return plain > 0 ? plain : null;

        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("pid", out var pid)
                && pid.TryGetInt32(out var value)
                && value > 0
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Kills <paramref name="pid"/> only when it is still the CodeGraph process that wrote the pid file: CodeGraph runs
    /// on its bundled <c>node</c>, and a process started after the file was written reused a dead process's id.
    /// </summary>
    internal static async Task<bool> KillCodeGraphProcessAsync(int pid, DateTime pidFileWrittenUtc, CancellationToken cancellationToken)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return false; }

        using (process)
        {
            try
            {
                if (process.HasExited
                    || !string.Equals(process.ProcessName, "node", StringComparison.OrdinalIgnoreCase)
                    || process.StartTime.ToUniversalTime() > pidFileWrittenUtc + PidFileClockSlack)
                    return false;

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(KillWaitTimeout, cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or TimeoutException)
            {
                // Exited meanwhile, not ours to inspect or kill, or still dying; deleting the folder tells.
                return false;
            }
        }
    }

    /// <summary>Runs <c>codegraph uninit -f</c>, which deletes <c>.codegraph</c> in <paramref name="root"/>.</summary>
    public Task<CommandLineResult> UninitAsync(string executable, string root, CancellationToken cancellationToken) =>
        commandLine.RunAsync(executable, ["uninit", "-f", root], root, [], cancellationToken, timeout: UninitTimeout);

    internal static string? FirstLine(string? text)
    {
        var line = text?.Trim().Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    private static string Key(string root) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed record RunningInit(CancellationTokenSource Cancel, Task Completion);
}
