using System.Text.Json;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// "Kill and remove" in the Worker: <see cref="TerminateBlockingProcessesCommand"/> ends a selected process only when a fresh
/// inspection still finds it holding a Feature folder, with the same start time, and not protected; plus the pure rules that
/// decide protection and attribution, and the isolated-scan wire format.
/// </summary>
public sealed class TerminateBlockingProcessesCommandTests : IDisposable
{
    private static readonly DateTime Started = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-kill-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Ends_a_selected_process_that_still_holds_the_folder_and_reports_the_fresh_inspection()
    {
        var folder = Folder("api");
        var inspector = new FakeInspector(Holder(7, "claude", canTerminate: true));
        var terminator = new FakeTerminator();

        var result = await NewCommand(inspector, terminator).ExecuteAsync(Request([folder], (7, Started)));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(TerminateProcessOutcome.Killed, Assert.Single(result.Outcomes!).Outcome);
        Assert.Equal([(7, Started)], terminator.Calls);
        Assert.Equal(2, inspector.Calls);
        Assert.Equal(folder, Assert.Single(result.Results!).Path);
    }

    [Fact]
    public async Task A_different_start_time_means_a_reused_process_id_and_is_never_ended()
    {
        var inspector = new FakeInspector(Holder(7, "claude", canTerminate: true));
        var terminator = new FakeTerminator();

        var result = await NewCommand(inspector, terminator).ExecuteAsync(Request([Folder("api")], (7, Started.AddSeconds(1))));

        Assert.Equal(TerminateProcessOutcome.StartTimeChanged, Assert.Single(result.Outcomes!).Outcome);
        Assert.Empty(terminator.Calls);
    }

    [Fact]
    public async Task A_selection_without_a_start_time_is_never_ended()
    {
        var terminator = new FakeTerminator();

        var result = await NewCommand(new FakeInspector(Holder(7, "claude", canTerminate: true)), terminator)
            .ExecuteAsync(Request([Folder("api")], (7, null)));

        Assert.Equal(TerminateProcessOutcome.StartTimeChanged, Assert.Single(result.Outcomes!).Outcome);
        Assert.Empty(terminator.Calls);
    }

    [Fact]
    public async Task A_process_no_longer_holding_the_folder_is_left_alone()
    {
        var terminator = new FakeTerminator { Running = [9] };

        var result = await NewCommand(new FakeInspector(), terminator)
            .ExecuteAsync(Request([Folder("api")], (9, Started), (10, Started)));

        Assert.Equal(
            [TerminateProcessOutcome.NotHoldingAnymore, TerminateProcessOutcome.AlreadyExited],
            result.Outcomes!.Select(o => o.Outcome));
        Assert.Empty(terminator.Calls);
    }

    [Theory]
    [InlineData(BlockingProcessProtectedReason.Explorer, TerminateProcessOutcome.Protected)]
    [InlineData(BlockingProcessProtectedReason.System, TerminateProcessOutcome.Protected)]
    [InlineData(BlockingProcessProtectedReason.GrayMoon, TerminateProcessOutcome.Protected)]
    [InlineData(BlockingProcessProtectedReason.AccessDenied, TerminateProcessOutcome.AccessDenied)]
    public async Task Protected_processes_are_never_ended(string protectedReason, string expected)
    {
        var terminator = new FakeTerminator();
        var holder = Holder(7, "explorer", canTerminate: false) with { ProtectedReason = protectedReason };

        var result = await NewCommand(new FakeInspector(holder), terminator).ExecuteAsync(Request([Folder("api")], (7, Started)));

        Assert.Equal(expected, Assert.Single(result.Outcomes!).Outcome);
        Assert.Empty(terminator.Calls);
    }

    [Fact]
    public async Task Requires_absolute_paths_and_a_selection()
    {
        var command = NewCommand(new FakeInspector(), new FakeTerminator());

        Assert.False((await command.ExecuteAsync(new TerminateBlockingProcessesRequest { Processes = [new() { ProcessId = 1 }] })).Success);
        Assert.False((await command.ExecuteAsync(Request(["relative\\path"], (1, Started)))).Success);
        Assert.False((await command.ExecuteAsync(new TerminateBlockingProcessesRequest { Paths = [Folder("api")] })).Success);
    }

    [Theory]
    [InlineData("explorer", BlockingProcessKind.Explorer, BlockingProcessProtectedReason.Explorer)]
    [InlineData("svchost", BlockingProcessKind.Service, BlockingProcessProtectedReason.Service)]
    [InlineData("csrss", BlockingProcessKind.Application, BlockingProcessProtectedReason.System)]
    [InlineData("GrayMoon.Desktop", BlockingProcessKind.Application, BlockingProcessProtectedReason.GrayMoon)]
    [InlineData("graymoon-worker", BlockingProcessKind.Console, BlockingProcessProtectedReason.GrayMoon)]
    [InlineData("claude", BlockingProcessKind.Application, null)]
    [InlineData("pwsh", BlockingProcessKind.Console, null)]
    public void Fixed_protection_covers_system_services_explorer_and_graymoon(string name, BlockingProcessKind kind, string? expected)
    {
        var process = new BlockingProcessInfo(1234, name, $@"C:\bin\{name}.exe", null, kind, BlockingProcessReason.OpenFile);

        Assert.Equal(expected, BlockingProcessRules.FixedProtection(process));
    }

    [Fact]
    public void A_hit_is_attributed_to_the_most_specific_folder()
    {
        IReadOnlyList<IReadOnlyList<string>> roots =
        [
            [@"C:\features\login"],
            [@"C:\features\login\api", @"D:\real\api"],
            [@"C:\features\other"],
        ];

        Assert.Equal(1, BlockingProcessRules.MostSpecificRoot(roots, @"C:\features\login\api\src\a.cs"));
        Assert.Equal(1, BlockingProcessRules.MostSpecificRoot(roots, @"d:\REAL\api\bin"));
        Assert.Equal(0, BlockingProcessRules.MostSpecificRoot(roots, @"C:\features\login\README.md"));
        Assert.Equal(0, BlockingProcessRules.MostSpecificRoot(roots, @"C:\features\login"));
        Assert.Equal(-1, BlockingProcessRules.MostSpecificRoot(roots, @"C:\features\login2\x"));
    }

    [Theory]
    [InlineData(@"C:\f\repo", @"C:\f\repo", true)]
    [InlineData(@"C:\f\repo\", @"C:\f\repo", true)]
    [InlineData(@"c:\F\REPO\src", @"C:\f\repo", true)]
    [InlineData(@"C:\f\repo2", @"C:\f\repo", false)]
    [InlineData(@"C:\f", @"C:\f\repo", false)]
    public void Same_or_under_ignores_case_and_trailing_separators(string candidate, string root, bool expected)
    {
        Assert.Equal(expected, BlockingProcessRules.IsSameOrUnder(candidate, root));
    }

    [Fact]
    public void Isolated_scan_wire_format_keeps_identity_and_protection_and_rejects_a_wrong_count()
    {
        var outcome = new LockScanOutcome(
            [new FileLockInspectionResult([Holder(7, "claude", canTerminate: true) with { Reason = BlockingProcessReason.LoadedModule }], true, "slow")],
            HandleTableAvailable: true,
            HandleTableError: null);

        var json = JsonSerializer.Serialize(LockScanResponse.From(outcome));
        var back = JsonSerializer.Deserialize<LockScanResponse>(json)!;

        var process = Assert.Single(back.ToOutcome(1)!.Results[0].Processes);
        Assert.Equal(Started, process.StartTimeUtc);
        Assert.True(process.CanTerminate);
        Assert.Equal(BlockingProcessReason.LoadedModule, process.Reason);
        Assert.True(back.ToOutcome(1)!.Results[0].MayBeIncomplete);
        Assert.Null(back.ToOutcome(2));
    }

    [Fact]
    public void Child_process_launch_is_the_worker_apphost_or_dotnet_with_the_worker_dll()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var worker = typeof(WorkerOptions).Assembly;

        var apphost = LockScanChildProcess.ResolveLaunch(worker, @"C:\tools\graymoon-worker.exe");
        Assert.Equal(@"C:\tools\graymoon-worker.exe", apphost!.FileName);
        Assert.Equal([LockScanProtocol.Verb], apphost.Arguments);

        var viaDotnet = LockScanChildProcess.ResolveLaunch(worker, @"C:\Program Files\dotnet\dotnet.exe");
        Assert.Equal([worker.Location, LockScanProtocol.Verb], viaDotnet!.Arguments);

        Assert.Null(LockScanChildProcess.ResolveLaunch(typeof(FactAttribute).Assembly, @"C:\tools\testhost.exe"));
        Assert.Null(LockScanChildProcess.ResolveLaunch(worker, null));
    }

    // ---- helpers --------------------------------------------------------------------------------

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static BlockingProcessInfo Holder(int id, string name, bool canTerminate) =>
        new(id, name, $@"C:\bin\{name}.exe", null, BlockingProcessKind.Application, BlockingProcessReason.WorkingDirectory, Started, canTerminate,
            canTerminate ? null : BlockingProcessProtectedReason.AccessDenied);

    private static TerminateBlockingProcessesRequest Request(List<string> paths, params (int Id, DateTime? Start)[] processes) => new()
    {
        Paths = paths,
        Processes = processes.Select(p => new TerminateProcessSelection { ProcessId = p.Id, StartTimeUtc = p.Start }).ToList(),
    };

    private static TerminateBlockingProcessesCommand NewCommand(IFileLockInspector inspector, IProcessTerminator terminator) =>
        new(inspector, terminator, NullLogger<TerminateBlockingProcessesCommand>.Instance);

    private sealed class FakeInspector(params BlockingProcessInfo[] holders) : IFileLockInspector
    {
        public int Calls { get; private set; }

        public Task<FileLockInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new FileLockInspectionResult(holders, false, null));
        }
    }

    private sealed class FakeTerminator : IProcessTerminator
    {
        public List<(int, DateTime)> Calls { get; } = [];
        public HashSet<int> Running { get; init; } = [];

        public string Terminate(int processId, DateTime startTimeUtc, TimeSpan waitForExit)
        {
            Calls.Add((processId, startTimeUtc));
            return TerminateProcessOutcome.Killed;
        }

        public bool IsRunning(int processId) => Running.Contains(processId);
    }
}
