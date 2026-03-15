using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Common;

/// <summary>
/// Single place for process execution and DEBUG logging (safe parameters, elapsed ms, exit code, split timings).
/// </summary>
public sealed class CommandLineService(ILogger<CommandLineService> logger) : ICommandLineService
{
    public async Task<CommandLineResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        string? stdin = null,
        CancellationToken cancellationToken = default)
    {
        arguments ??= "";
        var sw = Stopwatch.StartNew();
        var resolvedWorkingDir = !string.IsNullOrWhiteSpace(workingDirectory) ? workingDirectory : Environment.CurrentDirectory;
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = resolvedWorkingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            startInfo.LoadUserProfile = false;

        var beforeStart = sw.ElapsedMilliseconds;
        using var process = Process.Start(startInfo);
        var afterStart = sw.ElapsedMilliseconds;
        var startCallMs = afterStart - beforeStart;

        if (process == null)
        {
            logger.LogDebug(
                "Command {Executable} {Parameters} failed to start. StartCall={StartCallMs}ms",
                fileName,
                LogSafe.ForLog(arguments),
                startCallMs);
            return new CommandLineResult(-1, null, "Failed to start process");
        }

        if (stdin != null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var afterReadSetup = sw.ElapsedMilliseconds;

        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(cancellationToken));
        var afterExit = sw.ElapsedMilliseconds;

        var stdout = await stdoutTask;
        var afterStdout = sw.ElapsedMilliseconds;
        var stderr = await stderrTask;
        var afterStderr = sw.ElapsedMilliseconds;

        logger.LogDebug(
            "Command {Executable} {Parameters} timings: StartCall={StartCallMs}ms, ReadSetup={ReadSetupMs}ms, WaitForExit={WaitForExitMs}ms, StdoutDone={StdoutDoneMs}ms, StderrDone={StderrDoneMs}ms, ExitCode={ExitCode}",
            fileName,
            LogSafe.ForLog(arguments),
            startCallMs,
            afterReadSetup - afterStart,
            afterExit - afterReadSetup,
            afterStdout - afterExit,
            afterStderr - afterStdout,
            process.ExitCode);

        return new CommandLineResult(process.ExitCode, stdout, stderr);
    }
}
