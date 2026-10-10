using System.Collections.Concurrent;
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

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan UninitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StopInitTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, RunningInit> _inits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds the executable; tests replace it so they do not depend on what the machine has installed.</summary>
    internal Func<string?> ExecutableResolver { get; init; } = FindOnPath;

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
