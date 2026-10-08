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
    public async Task RunAsync_MissingExecutable_ReturnsFailedStartInsteadOfThrowing()
    {
        var service = CreateService();

        var result = await service.RunAsync("graymoon-no-such-executable", "/version");

        Assert.Equal(-1, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stderr));
    }

    [Fact]
    public async Task RunAsync_ArgumentListOverload_MissingExecutable_ReturnsFailedStartInsteadOfThrowing()
    {
        var service = CreateService();

        var result = await service.RunAsync("graymoon-no-such-executable", (IReadOnlyList<string>)["/version"]);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stderr));
    }

    [Fact]
    public void ApplyNonInteractiveGitEnvironment_SetsGitTerminalPromptZero_ForGitExecutable()
    {
        var startInfo = new ProcessStartInfo { FileName = "git" };

        CommandLineService.ApplyNonInteractiveGitEnvironment(startInfo, "git");

        Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", startInfo.Environment["GCM_INTERACTIVE"]);
    }

    [Fact]
    public void ApplyNonInteractiveGitEnvironment_AppliesAmbientVariables_OnlyForGit()
    {
        var variables = new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "1" };
        using var _ = new GitProcessEnvironmentScope(variables);

        var git = new ProcessStartInfo { FileName = "git" };
        CommandLineService.ApplyNonInteractiveGitEnvironment(git, "git");
        var other = new ProcessStartInfo { FileName = "dotnet" };
        other.Environment.Remove("GIT_CONFIG_COUNT");
        CommandLineService.ApplyNonInteractiveGitEnvironment(other, "dotnet");

        Assert.Equal("1", git.Environment["GIT_CONFIG_COUNT"]);
        Assert.False(other.Environment.ContainsKey("GIT_CONFIG_COUNT"));
    }

    [Fact]
    public void ApplyNonInteractiveGitEnvironment_DoesNotSetGitTerminalPrompt_ForNonGitExecutable()
    {
        var startInfo = new ProcessStartInfo { FileName = "dotnet" };
        startInfo.Environment.Remove("GIT_TERMINAL_PROMPT");

        CommandLineService.ApplyNonInteractiveGitEnvironment(startInfo, "dotnet");

        Assert.False(startInfo.Environment.ContainsKey("GIT_TERMINAL_PROMPT"));
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
        using var workDir = TestProcess.CreateLargeStdoutWorkDirectory();
        var (fileName, arguments) = TestProcess.WriteLargeStdoutThenDrainStdin();
        var stdin = new byte[262_144];
        Array.Fill(stdin, (byte)'Y');

        // Wait on the actual condition (the process completes instead of deadlocking) with a
        // generous upper bound, rather than asserting a tight wall-clock time that is flaky
        // under CPU load from parallel test assemblies.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var result = await service.RunAsync(
            fileName,
            arguments,
            workingDirectory: workDir.Path,
            stdinBytes: stdin,
            cancellationToken: cts.Token,
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Stdout);
        Assert.True(result.Stdout.Length >= 200_000, $"Expected large stdout, got {result.Stdout?.Length ?? 0} chars.");
    }

    [Fact]
    public async Task RunAsync_StringStdinOverload_DoesNotDeadlock_WhenChildWritesStdoutWhileReadingStdin()
    {
        var service = CreateService();
        using var workDir = TestProcess.CreateLargeStdoutWorkDirectory();
        var (fileName, arguments) = TestProcess.WriteLargeStdoutThenDrainStdinAsArgumentsString();
        var stdin = new string('Y', 262_144);

        // Wait on the actual condition (the process completes instead of deadlocking) with a
        // generous upper bound, rather than asserting a tight wall-clock time that is flaky
        // under CPU load from parallel test assemblies.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var result = await service.RunAsync(
            fileName,
            arguments,
            workingDirectory: workDir.Path,
            stdin: stdin,
            cancellationToken: cts.Token,
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Stdout);
        Assert.True(result.Stdout.Length >= 200_000, $"Expected large stdout, got {result.Stdout?.Length ?? 0} chars.");
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
        /// <summary>Name of the 256 KB file the Windows large-stdout helper prints (see <see cref="CreateLargeStdoutWorkDirectory"/>).</summary>
        private const string LargeStdoutFileName = "large-stdout.txt";

        // Windows: cmd built-ins only (type, then more.com draining stdin to nul). This used to be a PowerShell
        // byte-by-byte loop, whose cold start plus interpreted loop could exceed the 30 s timeout on a loaded CI
        // runner while the test assemblies run in parallel; cmd starts and finishes in milliseconds. more.com is
        // called by full path so Git for Windows' Unix tools on PATH are never picked up instead.
        private const string WindowsWriteThenDrain = "type " + LargeStdoutFileName + " & %SystemRoot%\\System32\\more.com >nul";

        /// <summary>
        /// Temporary working directory for the large-stdout helpers; on Windows it holds the 256 KB file the child
        /// writes to stdout before it starts reading stdin. Deleted on dispose.
        /// </summary>
        public static TempDirectory CreateLargeStdoutWorkDirectory()
        {
            var directory = new TempDirectory(Directory.CreateTempSubdirectory("graymoon-cls-").FullName);
            File.WriteAllText(System.IO.Path.Combine(directory.Path, LargeStdoutFileName), new string('X', 262_144));
            return directory;
        }

        public static (string FileName, IReadOnlyList<string> Arguments) WriteLargeStdoutThenDrainStdin()
            => OperatingSystem.IsWindows()
                ? ("cmd.exe", ["/d", "/c", WindowsWriteThenDrain])
                : ("/bin/sh", ["-c", "dd if=/dev/zero bs=1024 count=256 status=none; cat >/dev/null"]);

        public static (string FileName, string Arguments) WriteLargeStdoutThenDrainStdinAsArgumentsString()
            => OperatingSystem.IsWindows()
                ? ("cmd.exe", $"/d /c \"{WindowsWriteThenDrain}\"")
                : ("/bin/sh", "-c \"dd if=/dev/zero bs=1024 count=256 status=none; cat >/dev/null\"");

        public sealed class TempDirectory(string path) : IDisposable
        {
            public string Path { get; } = path;

            public void Dispose()
            {
                try { Directory.Delete(Path, true); } catch { /* best-effort */ }
            }
        }
    }
}
