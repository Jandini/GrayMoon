using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Common;

/// <summary>
/// One process run with hard limits. Arguments are passed as a list (no shell, no quoting), stdout is read as bytes up
/// to <see cref="MaxStdoutBytes"/> and the process is stopped there, so a huge diff is never materialized just to be
/// trimmed. Nothing about the arguments, stdin or output is logged: callers pass prompts and source code through it.
/// </summary>
public sealed record BoundedProcessRequest(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }

    public byte[]? Stdin { get; init; }

    public int MaxStdoutBytes { get; init; } = 1_048_576;

    public int MaxStderrBytes { get; init; } = 16_384;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Environment changes for the child: a value sets the variable, null removes it.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
}

public sealed record BoundedProcessResult(
    int ExitCode,
    byte[] Stdout,
    bool StdoutTruncated,
    string Stderr,
    bool TimedOut,
    string? StartFailure)
{
    public bool Started => StartFailure is null;

    public string StdoutText => Encoding.UTF8.GetString(Stdout);
}

public interface IBoundedProcessRunner
{
    /// <summary>
    /// Runs the process. On timeout the process tree is killed and <see cref="BoundedProcessResult.TimedOut"/> is set; on
    /// cancellation it is killed and <see cref="OperationCanceledException"/> is thrown. A missing executable is a result
    /// with <see cref="BoundedProcessResult.StartFailure"/>, not an exception.
    /// </summary>
    Task<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken cancellationToken);
}

public sealed class BoundedProcessRunner(ILogger<BoundedProcessRunner> logger) : IBoundedProcessRunner
{
    public async Task<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var executable = Path.GetFileName(request.FileName);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.Stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);
        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                if (value is null)
                    startInfo.Environment.Remove(key);
                else
                    startInfo.Environment[key] = value;
            }
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new Win32Exception("The process did not start.");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            logger.LogDebug("Bounded process {Executable} did not start: {Reason}", executable, ex.Message);
            return new BoundedProcessResult(-1, [], false, string.Empty, false, ex.Message);
        }

        using (process)
        {
            using var timeoutCts = new CancellationTokenSource(request.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, request.MaxStdoutBytes, stopWhenFull: true, linked.Token);
            var stderrTask = ReadCappedAsync(process.StandardError.BaseStream, request.MaxStderrBytes, stopWhenFull: false, linked.Token);

            try
            {
                if (request.Stdin is not null)
                {
                    try
                    {
                        await process.StandardInput.BaseStream.WriteAsync(request.Stdin, linked.Token);
                        await process.StandardInput.BaseStream.FlushAsync(linked.Token);
                    }
                    catch (IOException)
                    {
                        // The child exited or closed stdin early; its exit code and stderr tell why.
                    }
                    finally
                    {
                        process.StandardInput.Close();
                    }
                }

                var stdout = await stdoutTask;
                if (stdout.Truncated)
                {
                    // Enough output: stop the process instead of draining (and buffering) the rest.
                    Kill(process);
                }

                await process.WaitForExitAsync(linked.Token);
                var stderr = await stderrTask;
                var exitCode = stdout.Truncated ? -1 : process.ExitCode;
                logger.LogDebug(
                    "Bounded process {Executable} finished in {ElapsedMs}ms (ExitCode={ExitCode}, StdoutBytes={StdoutBytes}, Truncated={Truncated})",
                    executable, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, exitCode, stdout.Bytes.Length, stdout.Truncated);
                return new BoundedProcessResult(exitCode, stdout.Bytes, stdout.Truncated, Encoding.UTF8.GetString(stderr.Bytes), false, null);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                logger.LogDebug("Bounded process {Executable} timed out after {TimeoutSeconds}s and was killed", executable, request.Timeout.TotalSeconds);
                var partial = await DrainAsync(stdoutTask);
                return new BoundedProcessResult(-1, partial, false, $"Timed out after {request.Timeout.TotalSeconds:0}s.", true, null);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }
        }
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadCappedAsync(Stream stream, int maxBytes, bool stopWhenFull, CancellationToken cancellationToken)
    {
        var kept = new MemoryStream();
        var buffer = new byte[16_384];
        var truncated = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            var room = maxBytes - (int)kept.Length;
            if (room > 0)
                kept.Write(buffer, 0, Math.Min(room, read));

            if (read > room)
            {
                truncated = true;
                if (stopWhenFull)
                    break;
                // stderr: keep draining so the child never blocks on a full pipe, but keep nothing more.
            }
        }

        return (kept.ToArray(), truncated);
    }

    private static async Task<byte[]> DrainAsync(Task<(byte[] Bytes, bool Truncated)> task)
    {
        try
        {
            return (await task).Bytes;
        }
        catch
        {
            return [];
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exiting; nothing left to stop.
        }
    }
}
