using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Common.Tests;

public sealed class CommandLineServiceTests
{
    private static CommandLineService CreateService(int defaultTimeoutSeconds = 60)
        => new(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions { DefaultTimeoutSeconds = defaultTimeoutSeconds }));

    [Fact]
    public async Task RunAsync_CompletesNormally_WhenProcessFinishesBeforeTimeout()
    {
        var service = CreateService();
        var (fileName, arguments) = TestProcess.EchoHello();

        var result = await service.RunAsync(fileName, arguments, timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Stdout);
    }

    [Fact]
    public async Task RunAsync_KillsProcessAndReturnsSyntheticFailure_WhenItExceedsTheTimeout()
    {
        var service = CreateService();
        var sw = Stopwatch.StartNew();

        // Sleeps far longer than the 1s timeout below - the test only passes if the process is
        // actually killed rather than the call hanging until the sleep finishes.
        var (fileName, arguments) = TestProcess.SleepSeconds(30);
        var result = await service.RunAsync(fileName, arguments, timeout: TimeSpan.FromSeconds(1));

        sw.Stop();

        Assert.Equal(-1, result.ExitCode);
        Assert.NotNull(result.Stderr);
        Assert.Contains("timed out", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"Expected the hung process to be killed quickly, took {sw.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_UsesInjectedDefaultTimeout_WhenCallerPassesNone()
    {
        var service = CreateService(defaultTimeoutSeconds: 1);
        var sw = Stopwatch.StartNew();
        var (fileName, arguments) = TestProcess.SleepSeconds(30);

        var result = await service.RunAsync(fileName, arguments);

        sw.Stop();

        Assert.Equal(-1, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"Expected the injected default timeout to apply, took {sw.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_ArgumentListOverload_KillsProcessAndReturnsSyntheticFailure_WhenItExceedsTheTimeout()
    {
        var service = CreateService();
        var sw = Stopwatch.StartNew();
        var (fileName, arguments) = TestProcess.SleepSecondsAsArgumentList(30);

        var result = await service.RunAsync(fileName, arguments, timeout: TimeSpan.FromSeconds(1));

        sw.Stop();

        Assert.Equal(-1, result.ExitCode);
        Assert.NotNull(result.Stderr);
        Assert.Contains("timed out", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"Expected the hung process to be killed quickly, took {sw.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_InfiniteTimeout_DoesNotKillProcess_UsedForUnboundedCloneTier()
    {
        // Timeout.InfiniteTimeSpan is the sentinel GitProcessRunner passes for "clone" when
        // GitProcessOptions.CloneTimeoutSeconds is 0 (the default) - CancellationTokenSource(TimeSpan)
        // never schedules cancellation for it, so a long-running command completes normally.
        var service = CreateService();
        var (fileName, arguments) = TestProcess.SleepMilliseconds(500);

        var result = await service.RunAsync(fileName, arguments, timeout: Timeout.InfiniteTimeSpan);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_StillThrowsOperationCanceledException_NotSyntheticFailure()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        var (fileName, arguments) = TestProcess.SleepSeconds(30);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(
            fileName,
            arguments,
            cancellationToken: cts.Token,
            timeout: TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task RunAsync_ArgumentListOverload_DoesNotDeadlock_WhenChildWritesStdoutWhileReadingStdin()
    {
        // Reproduces the classic redirected-process pipe deadlock: child fills stdout before finishing
        // stdin, parent used to write-all-stdin before starting stdout consumers (and before the timeout
        // CTS existed). With consumers+timeout first, this completes instead of hanging forever.
        var service = CreateService();
        var (fileName, arguments) = TestProcess.WriteLargeStdoutThenDrainStdin();
        var stdin = new byte[262_144];
        Array.Fill(stdin, (byte)'Y');

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sw = Stopwatch.StartNew();
        var result = await service.RunAsync(
            fileName,
            arguments,
            stdinBytes: stdin,
            cancellationToken: cts.Token,
            timeout: TimeSpan.FromSeconds(15));
        sw.Stop();

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Stdout);
        Assert.True(result.Stdout.Length >= 200_000, $"Expected large stdout, got {result.Stdout?.Length ?? 0} chars.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"Expected no pipe deadlock, took {sw.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_StringStdinOverload_DoesNotDeadlock_WhenChildWritesStdoutWhileReadingStdin()
    {
        var service = CreateService();
        var (fileName, arguments) = TestProcess.WriteLargeStdoutThenDrainStdinAsArgumentsString();
        var stdin = new string('Y', 262_144);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sw = Stopwatch.StartNew();
        var result = await service.RunAsync(
            fileName,
            arguments,
            stdin: stdin,
            cancellationToken: cts.Token,
            timeout: TimeSpan.FromSeconds(15));
        sw.Stop();

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Stdout);
        Assert.True(result.Stdout.Length >= 200_000, $"Expected large stdout, got {result.Stdout?.Length ?? 0} chars.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"Expected no pipe deadlock, took {sw.Elapsed}.");
    }

    /// <summary>
    /// Cross-platform process invocations for lifecycle tests (Windows dev/CI and Linux GitHub Actions).
    /// On Unix, invoke <c>/bin/echo</c> and <c>/bin/sleep</c> directly - shell <c>-c</c> requires the
    /// script as a single argv token, which is easy to get wrong when building an Arguments string.
    /// </summary>
    private static class TestProcess
    {
        public static (string FileName, string Arguments) EchoHello()
            => OperatingSystem.IsWindows()
                ? ("cmd.exe", "/c echo hello")
                : ("/bin/echo", "hello");

        public static (string FileName, string Arguments) SleepSeconds(int seconds)
            => OperatingSystem.IsWindows()
                ? ("powershell.exe", $"-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds {seconds}\"")
                : ("/bin/sleep", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public static (string FileName, IReadOnlyList<string> Arguments) SleepSecondsAsArgumentList(int seconds)
            => OperatingSystem.IsWindows()
                ? ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", $"Start-Sleep -Seconds {seconds}"])
                : ("/bin/sleep", [seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        public static (string FileName, string Arguments) SleepMilliseconds(int milliseconds)
            => OperatingSystem.IsWindows()
                ? ("powershell.exe", $"-NoProfile -NonInteractive -Command \"Start-Sleep -Milliseconds {milliseconds}; exit 0\"")
                : ("/bin/sleep", (milliseconds / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>
        /// Writes ~256 KiB to stdout, then drains stdin to EOF. Used to detect stdin-before-stdout
        /// pipe deadlocks in <see cref="CommandLineService"/>.
        /// </summary>
        public static (string FileName, IReadOnlyList<string> Arguments) WriteLargeStdoutThenDrainStdin()
            => OperatingSystem.IsWindows()
                ? ("powershell.exe",
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "$out = [Console]::OpenStandardOutput(); $data = New-Object byte[] 262144; for ($i = 0; $i -lt $data.Length; $i++) { $data[$i] = 88 }; $out.Write($data, 0, $data.Length); $out.Flush(); $in = [Console]::OpenStandardInput(); $buf = New-Object byte[] 4096; while ($in.Read($buf, 0, $buf.Length) -gt 0) { }",
                ])
                : ("/bin/sh", ["-c", "dd if=/dev/zero bs=1024 count=256 status=none; cat >/dev/null"]);

        public static (string FileName, string Arguments) WriteLargeStdoutThenDrainStdinAsArgumentsString()
            => OperatingSystem.IsWindows()
                ? ("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"$out = [Console]::OpenStandardOutput(); $data = New-Object byte[] 262144; for ($i = 0; $i -lt $data.Length; $i++) { $data[$i] = 88 }; $out.Write($data, 0, $data.Length); $out.Flush(); $in = [Console]::OpenStandardInput(); $buf = New-Object byte[] 4096; while ($in.Read($buf, 0, $buf.Length) -gt 0) { }\"")
                : ("/bin/sh", "-c \"dd if=/dev/zero bs=1024 count=256 status=none; cat >/dev/null\"");
    }
}
