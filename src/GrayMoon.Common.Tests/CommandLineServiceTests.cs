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

        var result = await service.RunAsync("cmd.exe", "/c echo hello", timeout: TimeSpan.FromSeconds(30));

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
        var result = await service.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
            timeout: TimeSpan.FromSeconds(1));

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

        var result = await service.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"");

        sw.Stop();

        Assert.Equal(-1, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"Expected the injected default timeout to apply, took {sw.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_ArgumentListOverload_KillsProcessAndReturnsSyntheticFailure_WhenItExceedsTheTimeout()
    {
        var service = CreateService();
        var sw = Stopwatch.StartNew();

        var result = await service.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
            timeout: TimeSpan.FromSeconds(1));

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

        var result = await service.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Start-Sleep -Milliseconds 500; exit 0\"",
            timeout: Timeout.InfiniteTimeSpan);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_StillThrowsOperationCanceledException_NotSyntheticFailure()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
            cancellationToken: cts.Token,
            timeout: TimeSpan.FromSeconds(30)));
    }
}
