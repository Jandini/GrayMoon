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

        var suppressStreamLogging = ShouldSuppressStreamLogging(fileName, arguments);
        var suppressOverlayStdout = IsGitStatusCommand(fileName, arguments);
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
            segment => OnStreamSegment(fileName, suppressStreamLogging, suppressOverlayStdout, AgentCommandStreamKind.Stdout, segment),
            cancellationToken);
        var stderrTask = ConsumeStreamAsync(
            process.StandardError,
            segment => OnStreamSegment(fileName, suppressStreamLogging, suppressOverlay: false, stderrStreamKind, segment),
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

        var suppressStreamLogging = ShouldSuppressStreamLogging(fileName, arguments);
        var suppressOverlayStdout = IsGitStatusCommand(fileName, arguments);
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
            segment => OnStreamSegment(fileName, suppressStreamLogging, suppressOverlayStdout, AgentCommandStreamKind.Stdout, segment),
            cancellationToken);
        var stderrTask = ConsumeStreamPreservingLineEndingsAsync(
            process.StandardError,
            segment => OnStreamSegment(fileName, suppressStreamLogging, suppressOverlay: false, stderrStreamKind, segment),
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

    /// <summary>
    /// Git diff/show file-content commands can return large payloads; skip per-line DEBUG stream logs while
    /// still capturing stdout/stderr on the result and forwarding to any ambient overlay sink.
    /// </summary>
    private static bool ShouldSuppressStreamLogging(string fileName, string arguments)
    {
        if (!string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase))
            return false;

        var trimmed = arguments.TrimStart();
        if (trimmed.StartsWith("diff", StringComparison.Ordinal)
            && (trimmed.Length == 4 || trimmed[4] == ' '))
            return true;

        if (IsGitStatusCommand(fileName, arguments))
            return true;

        if (!trimmed.StartsWith("show ", StringComparison.Ordinal))
            return false;

        return trimmed.AsSpan(5).IndexOf(':') >= 0;
    }

    private static bool ShouldSuppressStreamLogging(string fileName, IReadOnlyList<string> arguments)
    {
        if (!string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase) || arguments.Count == 0)
            return false;

        if (string.Equals(arguments[0], "diff", StringComparison.Ordinal) || string.Equals(arguments[0], "status", StringComparison.Ordinal))
            return true;

        if (!string.Equals(arguments[0], "show", StringComparison.Ordinal))
            return false;

        for (var i = 1; i < arguments.Count; i++)
        {
            if (arguments[i].Contains(':', StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// <c>git status</c> output (this feature always uses <c>--porcelain=v2 -z</c>, NUL-delimited machine
    /// format) is not useful to a human reading the live overlay terminal, and Git Changes may run it for
    /// every repository in a workspace during a single refresh - so its stdout is kept out of the overlay
    /// entirely, not just out of the DEBUG log. The command-line echo (what was scanned) still shows;
    /// only the raw payload is hidden. stderr is never suppressed here so scan errors stay visible.
    /// </summary>
    private static bool IsGitStatusCommand(string fileName, string arguments)
    {
        if (!string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase))
            return false;

        var trimmed = arguments.TrimStart();
        return trimmed.StartsWith("status", StringComparison.Ordinal) && (trimmed.Length == 6 || trimmed[6] == ' ');
    }

    private static bool IsGitStatusCommand(string fileName, IReadOnlyList<string> arguments) =>
        string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase)
        && arguments.Count > 0
        && string.Equals(arguments[0], "status", StringComparison.Ordinal);

    private void OnStreamSegment(string fileName, bool suppressStreamLogging, bool suppressOverlay, AgentCommandStreamKind kind, string segment)
    {
        if (!suppressStreamLogging)
        {
            var label = kind == AgentCommandStreamKind.Stderr ? "stderr" : "stdout";
            logger.LogDebug("Command {Stream} ({Executable}): {Segment}", label, fileName, TruncateForLog(LogSafe.ForLog(segment)));
        }

        if (suppressOverlay)
        {
            return;
        }

        ReportAmbient(new CommandLineStreamEvent(kind, segment));
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
