using System.Diagnostics;
using System.Text;
using GrayMoon.Abstractions.Agent;
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
        var cwd = string.IsNullOrEmpty(workingDirectory) ? "." : workingDirectory;
        logger.LogDebug(
            "Command starting {Executable} {Parameters} cwd={WorkingDirectory}",
            fileName,
            LogSafe.ForLog(arguments),
            cwd);

        ReportAmbient(new CommandLineStreamEvent(AgentCommandStreamKind.CommandLine, $"$ {fileName} {LogSafe.ForLog(arguments)}"));

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
            segment =>
            {
                logger.LogDebug("Command stdout ({Executable}): {Segment}", fileName, TruncateForLog(LogSafe.ForLog(segment)));
                ReportAmbient(new CommandLineStreamEvent(AgentCommandStreamKind.Stdout, segment));
            },
            cancellationToken);
        var stderrTask = ConsumeStreamAsync(
            process.StandardError,
            segment =>
            {
                logger.LogDebug("Command stderr ({Executable}): {Segment}", fileName, TruncateForLog(LogSafe.ForLog(segment)));
                ReportAmbient(new CommandLineStreamEvent(AgentCommandStreamKind.Stderr, segment));
            },
            cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        logger.LogDebug("Command {Executable} {Parameters} completed in {ElapsedMs}ms (ExitCode={ExitCode})", fileName, LogSafe.ForLog(arguments), sw.ElapsedMilliseconds, process.ExitCode);
        return new CommandLineResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Reads stdout/stderr in small chunks and splits on <c>\n</c>, <c>\r\n</c>, and bare <c>\r</c>.
    /// Git clone/fetch progress uses carriage returns without newlines; <see cref="StreamReader.ReadLineAsync"/> would buffer until a newline and look "stuck".
    /// </summary>
    private static async Task<string> ConsumeStreamAsync(
        StreamReader reader,
        Action<string> onSegment,
        CancellationToken cancellationToken)
    {
        var aggregate = new StringBuilder();
        var segmentBuffer = new StringBuilder();
        var afterCr = false;
        var buffer = new char[8192];

        void FlushSegment()
        {
            if (segmentBuffer.Length == 0)
                return;

            var segment = segmentBuffer.ToString();
            segmentBuffer.Clear();
            aggregate.AppendLine(segment);
            onSegment(segment);
        }

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\r')
                {
                    FlushSegment();
                    afterCr = true;
                }
                else if (c == '\n')
                {
                    if (afterCr)
                        afterCr = false;
                    else
                        FlushSegment();
                }
                else
                {
                    if (afterCr)
                        afterCr = false;
                    segmentBuffer.Append(c);
                }
            }
        }

        FlushSegment();
        return aggregate.ToString();
    }

    private static string TruncateForLog(string text)
    {
        if (text.Length <= MaxLoggedStreamLength)
            return text;

        var omitted = text.Length - MaxLoggedStreamLength;
        return $"{text[..MaxLoggedStreamLength]} ... (truncated, {omitted} chars omitted)";
    }

    private static void ReportAmbient(CommandLineStreamEvent e)
    {
        var sink = CommandLineStreamAmbient.Current.Value;
        if (sink == null)
            return;

        try
        {
            sink(e);
        }
        catch
        {
            // Streaming must not break process execution
        }
    }
}
