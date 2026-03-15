using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Common;

/// <summary>
/// Single place for process execution and DEBUG logging (safe parameters, elapsed ms, exit code).
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
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
     
        using var process = Process.Start(startInfo);
        if (process == null)
        {
            sw.Stop();
            logger.LogDebug("Command {Executable} {Parameters} completed in {ElapsedMs}ms (ExitCode=-1)", fileName, LogSafe.ForLog(arguments), sw.ElapsedMilliseconds);
            return new CommandLineResult(-1, null, "Failed to start process");
        }

        if (stdin != null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        logger.LogDebug("Command {Executable} {Parameters} completed in {ElapsedMs}ms (ExitCode={ExitCode})", fileName, LogSafe.ForLog(arguments), sw.ElapsedMilliseconds, process.ExitCode);
        return new CommandLineResult(process.ExitCode, stdout, stderr);
    }
}
