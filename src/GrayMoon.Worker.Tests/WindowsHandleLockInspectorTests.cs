using System.Diagnostics;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Windows integration tests for the handle-table lock scan (<see cref="WindowsHandleLockInspector"/>) against real helper
/// processes: a shell in the folder, a folder handle, a program running from the folder, nested folders, a synchronous named
/// pipe that must not stall the scan, the isolated child process, and a user-confirmed kill. No-ops on other platforms.
/// </summary>
[Trait("Category", "PullRequest")]
public sealed class WindowsHandleLockInspectorTests : IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-hlock-").FullName;
    private readonly List<Process> _helpers = [];

    public void Dispose()
    {
        foreach (var helper in _helpers)
            Stop(helper);
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Shell_in_the_folder_is_reported_with_its_identity_and_can_be_ended()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var folder = Directory.CreateDirectory(Path.Combine(_root, "shell", "sub")).Parent!.FullName;
        var helper = await StartReadyAsync("cmd.exe", "/c \"echo ready& ping -n 120 127.0.0.1 >nul\"", Path.Combine(folder, "sub"));
        var inspector = NewInspector();

        var found = await inspector.InspectAsync(folder);

        var shell = Assert.Single(found.Processes, p => p.ProcessId == helper.Id);
        Assert.Equal(BlockingProcessReason.WorkingDirectory, shell.Reason);
        Assert.True(shell.CanTerminate);
        Assert.NotNull(shell.StartTimeUtc);

        // Select everything that can be ended (the shell and the ping it started), as the dialog does by default.
        var command = new TerminateBlockingProcessesCommand(inspector, new ProcessTerminator(), NullLogger<TerminateBlockingProcessesCommand>.Instance);
        var result = await command.ExecuteAsync(new TerminateBlockingProcessesRequest
        {
            Paths = [folder],
            Processes = found.Processes
                .Where(p => p.CanTerminate)
                .Select(p => new TerminateProcessSelection { ProcessId = p.ProcessId, StartTimeUtc = p.StartTimeUtc })
                .ToList(),
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(TerminateProcessOutcome.Killed, result.Outcomes!.Single(o => o.ProcessId == helper.Id).Outcome);
        Assert.True(helper.WaitForExit(10_000));
        Assert.DoesNotContain(result.Results![0].BlockingProcesses!, p => p.ProcessId == helper.Id);
    }

    [Fact]
    public async Task Folder_handle_and_open_file_are_reported_as_open()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var watched = Directory.CreateDirectory(Path.Combine(_root, "watched")).FullName;
        var held = Directory.CreateDirectory(Path.Combine(_root, "held")).FullName;
        var file = Path.Combine(held, "a.txt");
        await File.WriteAllTextAsync(file, "a\n");

        // A file watcher holds a handle on the folder itself (no file inside open): Restart Manager cannot see this.
        var script =
            $"$w=New-Object IO.FileSystemWatcher '{watched.Replace("'", "''")}'; $w.EnableRaisingEvents=$true; " +
            $"$f=[IO.File]::Open('{file.Replace("'", "''")}','Open','Read','None'); " +
            "[Console]::Out.WriteLine('ready'); [Console]::Out.Flush(); Start-Sleep -Seconds 120";
        var helper = await StartReadyAsync("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"", _root);

        var results = await NewInspector().InspectManyAsync([watched, held]);

        Assert.Equal(BlockingProcessReason.OpenFile, Assert.Single(results[0].Processes, p => p.ProcessId == helper.Id).Reason);
        Assert.Equal(BlockingProcessReason.OpenFile, Assert.Single(results[1].Processes, p => p.ProcessId == helper.Id).Reason);
    }

    [Fact]
    public async Task Program_running_from_the_folder_is_reported_as_a_loaded_module()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var folder = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var tool = Path.Combine(folder, "tool.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), tool);

        // Started from outside the folder: only the running image ties it to the folder.
        var helper = await StartReadyAsync(tool, "/c \"echo ready& ping -n 120 127.0.0.1 >nul\"", _root);

        var result = await NewInspector().InspectAsync(folder);

        Assert.Equal(BlockingProcessReason.LoadedModule, Assert.Single(result.Processes, p => p.ProcessId == helper.Id).Reason);
    }

    [Fact]
    public async Task Nested_folders_report_a_process_only_under_the_most_specific_one()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var outer = Directory.CreateDirectory(Path.Combine(_root, "feature")).FullName;
        var inner = Directory.CreateDirectory(Path.Combine(outer, "api")).FullName;
        var helper = await StartReadyAsync("cmd.exe", "/c \"echo ready& ping -n 120 127.0.0.1 >nul\"", inner);

        var results = await NewInspector().InspectManyAsync([outer, inner]);

        Assert.DoesNotContain(results[0].Processes, p => p.ProcessId == helper.Id);
        Assert.Contains(results[1].Processes, p => p.ProcessId == helper.Id);
    }

    [Fact]
    public async Task A_blocked_synchronous_named_pipe_does_not_stall_the_scan()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // A synchronous pipe server blocked in WaitForConnection is the classic handle that hangs a name query.
        var script =
            "$p=New-Object IO.Pipes.NamedPipeServerStream('graymoon-test-" + Guid.NewGuid().ToString("N") + "'); " +
            "[Console]::Out.WriteLine('ready'); [Console]::Out.Flush(); $p.WaitForConnection()";
        await StartReadyAsync("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"", _root);
        var folder = Directory.CreateDirectory(Path.Combine(_root, "idle")).FullName;

        var stopwatch = Stopwatch.StartNew();
        var result = await NewInspector().InspectAsync(folder);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Scan took {stopwatch.Elapsed}");
        Assert.Empty(result.Processes);
    }

    [Fact]
    public async Task The_worker_never_reports_itself()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var folder = Directory.CreateDirectory(Path.Combine(_root, "self")).FullName;
        var file = Path.Combine(folder, "a.txt");
        await File.WriteAllTextAsync(file, "a\n");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await NewInspector().InspectAsync(folder);
            Assert.DoesNotContain(result.Processes, p => p.ProcessId == Environment.ProcessId);
        }
    }

    [Fact]
    public async Task Isolated_child_process_returns_the_same_result()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workerExe = Path.Combine(AppContext.BaseDirectory, "graymoon-worker.exe");
        if (!File.Exists(workerExe))
            return;

        var folder = Directory.CreateDirectory(Path.Combine(_root, "isolated")).FullName;
        var helper = await StartReadyAsync("cmd.exe", "/c \"echo ready& ping -n 120 127.0.0.1 >nul\"", folder);
        var child = new LockScanChildProcess(
            NullLogger<LockScanChildProcess>.Instance,
            new LockScanChildProcess.LaunchCommand(workerExe, [LockScanProtocol.Verb]));

        var result = await child.RunAsync([folder], [Environment.ProcessId], TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(LockScanChildProcess.ChildStatus.Completed, result.Status);
        Assert.Contains(result.Outcome!.Results[0].Processes, p => p.ProcessId == helper.Id && p.Reason == BlockingProcessReason.WorkingDirectory);
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>In-process scan (no child process), as under a test host.</summary>
    private static WindowsHandleLockInspector NewInspector()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        return new WindowsHandleLockInspector(
            new WindowsFileLockInspector(NullLogger<WindowsFileLockInspector>.Instance),
            new LockScanChildProcess(NullLogger<LockScanChildProcess>.Instance, launch: null),
            // Generous budget: these tests check what is found, not how fast, and the full suite runs many processes in parallel.
            Options.Create(new WorkerOptions { LockInspectionTimeoutSeconds = 30 }),
            NullLogger<WindowsHandleLockInspector>.Instance);
    }

    private async Task<Process> StartReadyAsync(string fileName, string arguments, string workingDirectory)
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
        var helper = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        _helpers.Add(helper);

        using var cts = new CancellationTokenSource(ReadyTimeout);
        while (true)
        {
            var line = await helper.StandardOutput.ReadLineAsync(cts.Token);
            if (line is null)
                throw new InvalidOperationException($"Helper exited before it was ready: {await helper.StandardError.ReadToEndAsync()}");
            if (line.Trim() == "ready")
                return helper;
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
        finally
        {
            helper.Dispose();
        }
    }
}
