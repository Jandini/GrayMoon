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

    private const int MaxMirrorLineLength = 4096;

    public async Task<CommandLineResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        string? stdin = null,
        CancellationToken cancellationToken = default,
        bool streamStderrAsStdout = false,
        bool mirrorFailureOutputAsStderr = false)
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

        var stderrStreamKind = streamStderrAsStdout ? AgentCommandStreamKind.Stdout : AgentCommandStreamKind.Stderr;

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Without this, redirected output defaults to the ambient console codepage (e.g. an OEM
            // codepage on Windows) instead of UTF-8, mangling any non-ASCII output (em dashes, emoji,
            // accented names) from git/dotnet into mojibake.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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
                ReportAmbient(new CommandLineStreamEvent(stderrStreamKind, segment));
            },
            cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        logger.LogDebug("Command {Executable} {Parameters} completed in {ElapsedMs}ms (ExitCode={ExitCode})", fileName, LogSafe.ForLog(arguments), sw.ElapsedMilliseconds, process.ExitCode);

        if (mirrorFailureOutputAsStderr && process.ExitCode != 0)
            MirrorCombinedOutputAsStderr(stdout, stderr);

        return new CommandLineResult(process.ExitCode, stdout, stderr);
    }

    public async Task<CommandLineResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        byte[]? stdinBytes = null,
        CancellationToken cancellationToken = default,
        bool streamStderrAsStdout = false,
        bool mirrorFailureOutputAsStderr = false)
    {
        arguments ??= [];
        var sw = Stopwatch.StartNew();
        var cwd = string.IsNullOrEmpty(workingDirectory) ? "." : workingDirectory;
        var loggedArguments = LogSafe.ForLog(string.Join(' ', arguments));
        logger.LogDebug(
            "Command starting {Executable} {Parameters} cwd={WorkingDirectory}",
            fileName,
            loggedArguments,
            cwd);

        ReportAmbient(new CommandLineStreamEvent(AgentCommandStreamKind.CommandLine, $"$ {fileName} {loggedArguments}"));

        var stderrStreamKind = streamStderrAsStdout ? AgentCommandStreamKind.Stdout : AgentCommandStreamKind.Stderr;

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinBytes != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            // See the other RunAsync overload: force UTF-8 for redirected output so git/dotnet content
            // (e.g. git show file contents) round-trips exactly instead of via the ambient console codepage.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            sw.Stop();
            logger.LogDebug("Command {Executable} {Parameters} completed in {ElapsedMs}ms (ExitCode=-1)", fileName, loggedArguments, sw.ElapsedMilliseconds);
            return new CommandLineResult(-1, null, "Failed to start process");
        }

        if (stdinBytes != null)
        {
            // Write raw bytes directly to the stream so the exact NUL-delimited UTF-8 payload is
            // transmitted, bypassing any console-encoding assumptions StandardInput's StreamWriter would apply.
            await process.StandardInput.BaseStream.WriteAsync(stdinBytes, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        var stdoutTask = ConsumeStreamPreservingLineEndingsAsync(
            process.StandardOutput,
            segment =>
            {
                logger.LogDebug("Command stdout ({Executable}): {Segment}", fileName, TruncateForLog(LogSafe.ForLog(segment)));
                ReportAmbient(new CommandLineStreamEvent(AgentCommandStreamKind.Stdout, segment));
            },
            cancellationToken);
        var stderrTask = ConsumeStreamPreservingLineEndingsAsync(
            process.StandardError,
            segment =>
            {
                logger.LogDebug("Command stderr ({Executable}): {Segment}", fileName, TruncateForLog(LogSafe.ForLog(segment)));
                ReportAmbient(new CommandLineStreamEvent(stderrStreamKind, segment));
            },
            cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        logger.LogDebug("Command {Executable} {Parameters} completed in {ElapsedMs}ms (ExitCode={ExitCode})", fileName, loggedArguments, sw.ElapsedMilliseconds, process.ExitCode);

        if (mirrorFailureOutputAsStderr && process.ExitCode != 0)
            MirrorCombinedOutputAsStderr(stdout, stderr);

        return new CommandLineResult(process.ExitCode, stdout, stderr);
    }

    private static void MirrorCombinedOutputAsStderr(string? stdout, string? stderr)
    {
        EmitLines(stdout);
        EmitLines(stderr);

        void EmitLines(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var line = raw.Length > MaxMirrorLineLength
                    ? string.Concat(raw.AsSpan(0, MaxMirrorLineLength), " …")
                    : raw;

                ReportAmbient(new CommandLineStreamEvent(AgentCommandStreamKind.Stderr, line));
            }
        }
    }

    /// <summary>
    /// Same segmentation as <see cref="ConsumeStreamAsync"/> for live overlay streaming, but the returned
    /// aggregate preserves every character exactly as read (no <see cref="StringBuilder.AppendLine(string?)"/>
    /// substitution of <see cref="Environment.NewLine"/> for the original line terminator). Required for git
    /// blob/file content, where the caller needs byte-faithful text - a file authored with LF endings must not
    /// come back as CRLF just because the process is running on Windows.
    /// </summary>
    private static async Task<string> ConsumeStreamPreservingLineEndingsAsync(
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
                aggregate.Append(c);

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
