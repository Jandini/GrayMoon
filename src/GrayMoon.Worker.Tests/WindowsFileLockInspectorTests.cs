using System.Diagnostics;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Windows integration tests for <see cref="WindowsFileLockInspector"/> against real helper processes: one holding a file open
/// (Restart Manager layer) and one whose current directory is inside the folder (working-directory layer). Each test also
/// checks that the blocker disappears once the helper exits. No-ops on other platforms.
/// </summary>
[Trait("Category", "PullRequest")]
public sealed class WindowsFileLockInspectorTests : IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-lock-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Process_holding_a_file_open_is_reported_and_disappears_after_it_exits()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var folder = Directory.CreateDirectory(Path.Combine(_root, "open-file", "nested")).FullName;
        var file = Path.Combine(folder, "held.txt");
        await File.WriteAllTextAsync(file, "held\n");
        var inspector = new WindowsFileLockInspector(NullLogger<WindowsFileLockInspector>.Instance);

        // PowerShell opens the file with no sharing, prints "ready", then waits until it is killed.
        var script = $"$f=[System.IO.File]::Open('{file.Replace("'", "''")}','Open','Read','None'); [Console]::Out.WriteLine('ready'); [Console]::Out.Flush(); Start-Sleep -Seconds 120";
        using var helper = StartHelper("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"", workingDirectory: _root);
        try
        {
            await WaitForReadyAsync(helper);

            var result = await inspector.InspectAsync(Path.Combine(_root, "open-file"));

            var blocker = Assert.Single(result.Processes, p => p.ProcessId == helper.Id);
            Assert.Equal(BlockingProcessReason.OpenFile, blocker.Reason);
            Assert.False(string.IsNullOrWhiteSpace(blocker.ProcessName));
            Assert.EndsWith("powershell.exe", blocker.ExecutablePath ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Stop(helper);
        }

        var after = await inspector.InspectAsync(Path.Combine(_root, "open-file"));
        Assert.DoesNotContain(after.Processes, p => p.ProcessId == helper.Id);
    }

    [Fact]
    public async Task Process_whose_current_directory_is_inside_the_folder_is_reported()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var folder = Directory.CreateDirectory(Path.Combine(_root, "cwd", "sub")).FullName;
        var inspector = new WindowsFileLockInspector(NullLogger<WindowsFileLockInspector>.Instance);

        // cmd keeps its start folder as its current directory and holds no file open, like a shell left in a Feature.
        using var helper = StartHelper("cmd.exe", "/c \"echo ready& ping -n 120 127.0.0.1 >nul\"", workingDirectory: folder);
        try
        {
            await WaitForReadyAsync(helper);

            var result = await inspector.InspectAsync(Path.Combine(_root, "cwd"));

            var blocker = Assert.Single(result.Processes, p => p.ProcessId == helper.Id);
            Assert.Equal(BlockingProcessReason.WorkingDirectory, blocker.Reason);
            Assert.Equal("cmd", blocker.ProcessName, ignoreCase: true);
        }
        finally
        {
            Stop(helper);
        }

        var after = await inspector.InspectAsync(Path.Combine(_root, "cwd"));
        Assert.DoesNotContain(after.Processes, p => p.ProcessId == helper.Id);
    }

    [Fact]
    public async Task Unused_folder_has_no_blockers_and_the_worker_never_reports_itself()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var folder = Directory.CreateDirectory(Path.Combine(_root, "idle")).FullName;
        var file = Path.Combine(folder, "a.txt");
        await File.WriteAllTextAsync(file, "a\n");
        var inspector = new WindowsFileLockInspector(NullLogger<WindowsFileLockInspector>.Instance);

        // The test process itself holds the file open; the inspector must never list its own process.
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await inspector.InspectAsync(folder);
            Assert.DoesNotContain(result.Processes, p => p.ProcessId == Environment.ProcessId);
        }

        var idle = await inspector.InspectAsync(folder);
        Assert.Empty(idle.Processes);
    }

    [Fact]
    public async Task Missing_path_returns_an_empty_result()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var inspector = new WindowsFileLockInspector(NullLogger<WindowsFileLockInspector>.Instance);

        var result = await inspector.InspectAsync(Path.Combine(_root, "missing"));

        Assert.Empty(result.Processes);
        Assert.False(result.MayBeIncomplete);
    }

    [Fact]
    public void File_collection_is_capped_and_reports_truncation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var folder = Directory.CreateDirectory(Path.Combine(_root, "many")).FullName;
        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(folder, $"f{i}.txt"), "x");

        var (files, truncated) = WindowsFileLockInspector.CollectFiles(folder, max: 3);

        Assert.Equal(3, files.Count);
        Assert.True(truncated);
    }

    [Theory]
    [InlineData(@"C:\f\repo", @"C:\f\repo", true)]
    [InlineData(@"C:\f\repo\", @"C:\f\repo", true)]
    [InlineData(@"c:\F\REPO\src", @"C:\f\repo", true)]
    [InlineData(@"C:\f\repo2", @"C:\f\repo", false)]
    [InlineData(@"C:\f", @"C:\f\repo", false)]
    public void Same_or_under_ignores_case_and_trailing_separators(string candidate, string root, bool expected)
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(expected, WindowsFileLockInspector.IsSameOrUnder(candidate, root));
    }

    private static Process StartHelper(string fileName, string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
    }

    private static async Task WaitForReadyAsync(Process helper)
    {
        using var cts = new CancellationTokenSource(ReadyTimeout);
        while (true)
        {
            var line = await helper.StandardOutput.ReadLineAsync(cts.Token);
            if (line is null)
                throw new InvalidOperationException($"Helper exited before it was ready: {await helper.StandardError.ReadToEndAsync()}");
            if (line.Trim() == "ready")
                return;
        }
    }

    private static void Stop(Process helper)
    {
        try
        {
            if (!helper.HasExited)
                helper.Kill(entireProcessTree: true);
            helper.WaitForExit(10_000);
        }
        catch
        {
            // Already gone.
        }
    }
}
