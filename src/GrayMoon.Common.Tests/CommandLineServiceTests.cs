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

    /// <summary>
    /// Cross-platform shell commands for process-lifecycle tests (Windows CI and Linux GitHub Actions).
    /// </summary>
    private static class TestProcess
    {
        public static (string FileName, string Arguments) EchoHello()
            => OperatingSystem.IsWindows()
                ? ("cmd.exe", "/c echo hello")
                : ("/bin/sh", "-c echo hello");

        public static (string FileName, string Arguments) SleepSeconds(int seconds)
            => OperatingSystem.IsWindows()
                ? ("powershell.exe", $"-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds {seconds}\"")
                : ("/bin/sh", $"-c sleep {seconds}");

        public static (string FileName, IReadOnlyList<string> Arguments) SleepSecondsAsArgumentList(int seconds)
            => OperatingSystem.IsWindows()
                ? ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", $"Start-Sleep -Seconds {seconds}"])
                : ("/bin/sh", ["-c", $"sleep {seconds}"]);

        public static (string FileName, string Arguments) SleepMilliseconds(int milliseconds)
            => OperatingSystem.IsWindows()
                ? ("powershell.exe", $"-NoProfile -NonInteractive -Command \"Start-Sleep -Milliseconds {milliseconds}; exit 0\"")
                : ("/bin/sh", $"-c sleep {milliseconds / 1000.0}");
    }
}
