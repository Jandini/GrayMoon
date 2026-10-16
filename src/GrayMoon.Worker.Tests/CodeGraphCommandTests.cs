using System.Collections.Concurrent;
using System.Diagnostics;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Common;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

public sealed class CodeGraphCommandTests : IDisposable
{
    private const string Executable = @"C:\tools\codegraph.cmd";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graymoon-codegraph-" + Guid.NewGuid().ToString("N"));
    private readonly string _target;

    public CodeGraphCommandTests()
    {
        _target = Path.Combine(_root, "features", "feat");
        Directory.CreateDirectory(_target);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Init_builds_where_only_the_committed_gitignore_exists()
    {
        var commandLine = new ScriptedCommandLine();
        // A committed .codegraph/.gitignore alone is not an index.
        Directory.CreateDirectory(Path.Combine(_target, ".codegraph"));
        File.WriteAllText(Path.Combine(_target, ".codegraph", ".gitignore"), "*\n");

        var response = await new InitCodeGraphCommand(CreateCli(commandLine))
            .ExecuteAsync(new InitCodeGraphRequest { TargetRoot = _target });
        await commandLine.NextCallAsync();

        Assert.Equal("Started", response.Outcome);
    }

    [Fact]
    public async Task Init_reports_a_missing_feature_root()
    {

        var response = await new InitCodeGraphCommand(CreateCli(new ScriptedCommandLine()))
            .ExecuteAsync(new InitCodeGraphRequest { TargetRoot = Path.Combine(_root, "gone") });

        Assert.Equal("TargetMissing", response.Outcome);
    }

    [Fact]
    public async Task Init_leaves_an_existing_feature_index_alone()
    {
        CreateIndex(_target);
        var commandLine = new ScriptedCommandLine();

        var response = await new InitCodeGraphCommand(CreateCli(commandLine))
            .ExecuteAsync(new InitCodeGraphRequest { TargetRoot = _target });

        Assert.Equal("AlreadyInitialized", response.Outcome);
        Assert.Empty(commandLine.Calls);
    }

    [Fact]
    public async Task Init_reports_when_codegraph_is_not_installed()
    {

        var response = await new InitCodeGraphCommand(CreateCli(new ScriptedCommandLine(), executable: null))
            .ExecuteAsync(new InitCodeGraphRequest { TargetRoot = _target });

        Assert.Equal("NotInstalled", response.Outcome);
    }

    [Fact]
    public async Task Init_starts_a_background_build_in_the_feature_root()
    {
        var commandLine = new ScriptedCommandLine { Block = true };
        var cli = CreateCli(commandLine);
        var command = new InitCodeGraphCommand(cli);

        var first = await command.ExecuteAsync(new InitCodeGraphRequest { TargetRoot = _target });
        var call = await commandLine.NextCallAsync();
        var second = await command.ExecuteAsync(new InitCodeGraphRequest { TargetRoot = _target });

        Assert.Equal("Started", first.Outcome);
        Assert.Equal("AlreadyRunning", second.Outcome);
        Assert.Equal(Executable, call.FileName);
        Assert.Equal(["init", "-y", "-v", _target], call.Arguments);
        Assert.Equal(_target, call.WorkingDirectory);

        Assert.True(await cli.StopInitAsync(_target, CancellationToken.None));
        Assert.False(cli.IsInitRunning(_target));
    }

    [Fact]
    public async Task Watch_reports_no_build_where_none_was_started()
    {
        var response = await new WatchCodeGraphInitCommand(CreateCli(new ScriptedCommandLine()))
            .ExecuteAsync(new WatchCodeGraphInitRequest { Root = _target });

        Assert.Equal("NotRunning", response.Outcome);
        Assert.Equal(0, response.Next);
    }

    [Fact]
    public async Task Watch_streams_a_running_build_and_resumes_where_it_stopped()
    {
        var commandLine = new ScriptedCommandLine { Block = true, Output = ["[0.1s] Phase: scanning", "[0.4s]   1/2 (50%)"] };
        var cli = CreateCli(commandLine, watchWait: TimeSpan.FromMilliseconds(50));
        await new InitCodeGraphCommand(cli).ExecuteAsync(new InitCodeGraphRequest { TargetRoot = _target });
        await commandLine.NextCallAsync();

        var first = await WatchAsync(cli, 0);
        await cli.StopInitAsync(_target, CancellationToken.None);
        var second = await WatchAsync(cli, first.Response.Next);

        Assert.Equal("Running", first.Response.Outcome);
        Assert.Contains("[0.1s] Phase: scanning", first.Lines);
        Assert.Contains("[0.4s]   1/2 (50%)", first.Lines);
        Assert.Equal("Stopped", second.Response.Outcome);
        Assert.DoesNotContain("[0.1s] Phase: scanning", second.Lines);
    }

    [Fact]
    public async Task Watch_gets_the_output_of_a_build_that_already_finished()
    {
        var commandLine = new ScriptedCommandLine { Output = ["done"] };
        var cli = CreateCli(commandLine);
        await new InitCodeGraphCommand(cli).ExecuteAsync(new InitCodeGraphRequest { TargetRoot = _target });
        await commandLine.NextCallAsync();
        while (cli.IsInitRunning(_target))
            await Task.Delay(10);

        var watched = await WatchAsync(cli, 0);

        Assert.Equal("Completed", watched.Response.Outcome);
        Assert.Contains("done", watched.Lines);
    }

    [Fact]
    public async Task Cancelling_a_watch_leaves_the_build_running()
    {
        var commandLine = new ScriptedCommandLine { Block = true };
        var cli = CreateCli(commandLine);
        await new InitCodeGraphCommand(cli).ExecuteAsync(new InitCodeGraphRequest { TargetRoot = _target });
        await commandLine.NextCallAsync();
        using var background = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WatchCodeGraphInitCommand(cli).ExecuteAsync(new WatchCodeGraphInitRequest { Root = _target }, background.Token));

        Assert.True(cli.IsInitRunning(_target));
        await cli.StopInitAsync(_target, CancellationToken.None);
    }

    [Fact]
    public async Task Uninit_does_nothing_without_an_index()
    {
        var commandLine = new ScriptedCommandLine();

        var response = await new UninitCodeGraphCommand(CreateCli(commandLine))
            .ExecuteAsync(new UninitCodeGraphRequest { Root = _target });

        Assert.Equal("NotInitialized", response.Outcome);
        Assert.Empty(commandLine.Calls);
    }

    [Fact]
    public async Task Uninit_runs_codegraph_uninit_in_the_feature_root()
    {
        CreateIndex(_target);
        var commandLine = new ScriptedCommandLine();

        var response = await new UninitCodeGraphCommand(CreateCli(commandLine))
            .ExecuteAsync(new UninitCodeGraphRequest { Root = _target });

        Assert.Equal("Removed", response.Outcome);
        var call = Assert.Single(commandLine.Calls);
        Assert.Equal(["uninit", "-f", _target], call.Arguments);
        Assert.Equal(_target, call.WorkingDirectory);
    }

    [Fact]
    public async Task Uninit_stops_a_running_build_first()
    {
        var commandLine = new ScriptedCommandLine { Block = true };
        var cli = CreateCli(commandLine);
        await new InitCodeGraphCommand(cli).ExecuteAsync(new InitCodeGraphRequest { TargetRoot = _target });
        await commandLine.NextCallAsync();
        // The cancelled build had already created its index folder.
        CreateIndex(_target);
        commandLine.Block = false;

        var response = await new UninitCodeGraphCommand(cli).ExecuteAsync(new UninitCodeGraphRequest { Root = _target });

        Assert.Equal("Removed", response.Outcome);
        Assert.False(cli.IsInitRunning(_target));
        Assert.Equal(["uninit", "-f", _target], commandLine.Calls.Last().Arguments);
    }

    [Fact]
    public async Task Uninit_kills_the_processes_named_in_the_index_before_uninit()
    {
        CreateIndex(_target);
        var folder = Path.Combine(_target, CodeGraphCli.DataFolderName);
        File.WriteAllText(Path.Combine(folder, "daemon.pid"),
            """{ "pid": 17400, "version": "1.6.2", "socketPath": "\\\\.\\pipe\\codegraph-x", "startedAt": 1 }""");
        File.WriteAllText(Path.Combine(folder, "writer.pid"), """{"pid":17400,"mode":"daemon","startedAt":1}""");
        File.WriteAllText(Path.Combine(folder, CodeGraphCli.LockFileName), "16948");
        var commandLine = new ScriptedCommandLine();
        var killed = new List<int>();

        var response = await new UninitCodeGraphCommand(CreateCli(commandLine, killed: killed))
            .ExecuteAsync(new UninitCodeGraphRequest { Root = _target });

        Assert.Equal("Removed", response.Outcome);
        Assert.Equal([17400, 16948], killed);
        Assert.Single(commandLine.Calls);
    }

    [Fact]
    public async Task Uninit_deletes_the_index_itself_when_uninit_fails()
    {
        CreateIndex(_target);
        var commandLine = new ScriptedCommandLine { Result = new CommandLineResult(1, "", "EBUSY: resource busy or locked\nmore") };

        var response = await new UninitCodeGraphCommand(CreateCli(commandLine))
            .ExecuteAsync(new UninitCodeGraphRequest { Root = _target });

        Assert.Equal("Removed", response.Outcome);
        Assert.False(Directory.Exists(Path.Combine(_target, CodeGraphCli.DataFolderName)));
    }

    [Fact]
    public async Task Uninit_deletes_the_index_when_codegraph_is_not_installed()
    {
        CreateIndex(_target);

        var response = await new UninitCodeGraphCommand(CreateCli(new ScriptedCommandLine(), executable: null))
            .ExecuteAsync(new UninitCodeGraphRequest { Root = _target });

        Assert.Equal("Removed", response.Outcome);
        Assert.False(Directory.Exists(Path.Combine(_target, CodeGraphCli.DataFolderName)));
    }

    [Fact]
    public async Task Uninit_reports_an_index_that_stays_held()
    {
        // Only Windows refuses to delete a file another handle has open.
        if (!OperatingSystem.IsWindows())
            return;

        CreateIndex(_target);
        var commandLine = new ScriptedCommandLine { Result = new CommandLineResult(1, "", "EBUSY: resource busy or locked\nmore") };
        var database = Path.Combine(_target, CodeGraphCli.DataFolderName, CodeGraphCli.DatabaseFileName);

        UninitCodeGraphResponse response;
        using (new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            response = await new UninitCodeGraphCommand(CreateCli(commandLine))
                .ExecuteAsync(new UninitCodeGraphRequest { Root = _target });
        }

        Assert.Equal("Failed", response.Outcome);
        Assert.Equal("EBUSY: resource busy or locked", response.Message);
    }

    [Fact]
    public async Task Killing_spares_a_process_that_is_not_codegraph()
    {
        CreateIndex(_target);
        // A reused pid: this test host is not CodeGraph's node.
        File.WriteAllText(Path.Combine(_target, CodeGraphCli.DataFolderName, "daemon.pid"), $$"""{"pid":{{Environment.ProcessId}}}""");

        var killed = await CreateCli(new ScriptedCommandLine(), realProcesses: true).KillIndexProcessesAsync(_target, CancellationToken.None);

        Assert.Equal(0, killed);
    }

    [Fact]
    public async Task Killing_ends_the_node_process_that_holds_the_lock()
    {
        var node = FindNode();
        if (node is null)
            return;

        CreateIndex(_target);
        using var holder = Process.Start(new ProcessStartInfo(node, ["-e", "setTimeout(() => {}, 60000)"]) { UseShellExecute = false })!;
        try
        {
            File.WriteAllText(Path.Combine(_target, CodeGraphCli.DataFolderName, CodeGraphCli.LockFileName), holder.Id.ToString());

            var killed = await CreateCli(new ScriptedCommandLine(), realProcesses: true).KillIndexProcessesAsync(_target, CancellationToken.None);

            Assert.Equal(1, killed);
            Assert.True(holder.HasExited);
        }
        finally
        {
            if (!holder.HasExited)
                holder.Kill();
        }
    }

    [Theory]
    [InlineData("16948", 16948)]
    [InlineData(" 16948\n", 16948)]
    [InlineData("""{"pid":17400,"version":"1.6.2"}""", 17400)]
    [InlineData("""{"pid":0}""", null)]
    [InlineData("-5", null)]
    [InlineData("", null)]
    [InlineData("not a pid", null)]
    [InlineData("[17400]", null)]
    public void ParsePid_reads_bare_and_json_pid_files(string text, int? expected) =>
        Assert.Equal(expected, CodeGraphCli.ParsePid(text));

    /// <summary>Runs one watch the way the Worker does, collecting the lines it streams.</summary>
    private async Task<(WatchCodeGraphInitResponse Response, List<string> Lines)> WatchAsync(CodeGraphCli cli, int from)
    {
        var lines = new List<string>();
        using var stream = new CommandLineStreamScope(e => lines.Add(e.Text));
        var response = await new WatchCodeGraphInitCommand(cli).ExecuteAsync(new WatchCodeGraphInitRequest { Root = _target, From = from });
        return (response, lines);
    }

    /// <summary>Records killed pids in <paramref name="killed"/> instead of killing, unless <paramref name="realProcesses"/>.</summary>
    private static CodeGraphCli CreateCli(
        ICommandLineService commandLine,
        string? executable = Executable,
        List<int>? killed = null,
        bool realProcesses = false,
        TimeSpan? watchWait = null) =>
        new(commandLine, new StubLifetime(), NullLogger<CodeGraphCli>.Instance)
        {
            ExecutableResolver = () => executable,
            WatchWait = watchWait ?? TimeSpan.FromSeconds(10),
            ProcessKiller = realProcesses
                ? CodeGraphCli.KillCodeGraphProcessAsync
                : (pid, _, _) =>
                {
                    killed?.Add(pid);
                    return Task.FromResult(true);
                },
            DeleteAttempts = 2,
            DeleteRetryDelay = TimeSpan.Zero,
        };

    /// <summary><c>node</c> on PATH, or null; the test that needs a real node process is skipped without one.</summary>
    private static string? FindNode()
    {
        var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim().Trim('"'), name))
            .FirstOrDefault(File.Exists);
    }

    private static void CreateIndex(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, CodeGraphCli.DataFolderName));
        File.WriteAllText(Path.Combine(root, CodeGraphCli.DataFolderName, CodeGraphCli.DatabaseFileName), "");
    }

    private sealed record Call(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory);

    /// <summary>Records list-argument calls; <see cref="Block"/> holds each call until it is cancelled.</summary>
    private sealed class ScriptedCommandLine : ICommandLineService
    {
        private readonly ConcurrentQueue<Call> _calls = new();
        private readonly SemaphoreSlim _called = new(0);

        public bool Block { get; set; }

        public CommandLineResult Result { get; init; } = new(0, "", "");

        /// <summary>Stdout lines each call streams to the ambient sink.</summary>
        public IReadOnlyList<string> Output { get; init; } = [];

        public IReadOnlyCollection<Call> Calls => _calls;

        public async Task<Call> NextCallAsync()
        {
            Assert.True(await _called.WaitAsync(TimeSpan.FromSeconds(10)), "codegraph was not started");
            return _calls.Last();
        }

        public Task<CommandLineResult> RunAsync(
            string fileName,
            string arguments,
            string? workingDirectory = null,
            string? stdin = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null) =>
            throw new NotSupportedException();

        public async Task<CommandLineResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            byte[]? stdinBytes = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null)
        {
            var block = Block;
            // Streamed like CommandLineService does, before the call is announced, so a test sees it once started.
            foreach (var line in Output)
                CommandLineStreamAmbient.Current.Value?.Invoke(new CommandLineStreamEvent(WorkerCommandStreamKind.Stdout, line));
            _calls.Enqueue(new Call(fileName, arguments.ToList(), workingDirectory));
            _called.Release();
            if (block)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return Result;
        }
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
