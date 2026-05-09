using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Common;

/// <summary>
/// Single place for process execution and DEBUG logging (safe parameters, elapsed ms, exit code).
/// </summary>
public sealed class CommandLineService(ILogger<CommandLineService> logger) : ICommandLineService
{
    private const int MaxLoggedStreamLength = 8_000;

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

        var stdoutTask = ConsumeStreamAsync(
            process.StandardOutput,
            line => logger.LogDebug("Command stdout ({Executable}): {Line}", fileName, TruncateForLog(LogSafe.ForLog(line))),
            cancellationToken);
        var stderrTask = ConsumeStreamAsync(
            process.StandardError,
            line => logger.LogDebug("Command stderr ({Executable}): {Line}", fileName, TruncateForLog(LogSafe.ForLog(line))),
            cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        logger.LogDebug("Command {Executable} {Parameters} completed in {ElapsedMs}ms (ExitCode={ExitCode})", fileName, LogSafe.ForLog(arguments), sw.ElapsedMilliseconds, process.ExitCode);
        return new CommandLineResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<string> ConsumeStreamAsync(
        StreamReader reader,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            sb.AppendLine(line);
            onLine(line);
        }

        return sb.ToString();
    }

    private static string TruncateForLog(string text)
    {
        if (text.Length <= MaxLoggedStreamLength)
            return text;

        var omitted = text.Length - MaxLoggedStreamLength;
        return $"{text[..MaxLoggedStreamLength]} ... (truncated, {omitted} chars omitted)";
    }
}
