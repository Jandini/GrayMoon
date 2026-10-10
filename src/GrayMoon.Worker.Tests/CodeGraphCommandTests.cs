using System.Collections.Concurrent;
using GrayMoon.Common;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

public sealed class CodeGraphCommandTests : IDisposable
{
    private const string Executable = @"C:\tools\codegraph.cmd";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graymoon-codegraph-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _target;

    public CodeGraphCommandTests()
    {
        _source = Path.Combine(_root, "ws");
        _target = Path.Combine(_root, "features", "feat");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_target);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Init_does_nothing_when_the_workspace_root_has_no_index()
    {
        var commandLine = new ScriptedCommandLine();
        // A committed .codegraph/.gitignore alone is not an index.
        Directory.CreateDirectory(Path.Combine(_source, ".codegraph"));
        File.WriteAllText(Path.Combine(_source, ".codegraph", ".gitignore"), "*\n");

        var response = await new InitCodeGraphCommand(CreateCli(commandLine))
            .ExecuteAsync(new InitCodeGraphRequest { SourceRoot = _source, TargetRoot = _target });

        Assert.Equal("SourceNotInitialized", response.Outcome);
        Assert.Empty(commandLine.Calls);
    }

    [Fact]
    public async Task Init_reports_a_missing_feature_root()
    {
        CreateIndex(_source);

        var response = await new InitCodeGraphCommand(CreateCli(new ScriptedCommandLine()))
            .ExecuteAsync(new InitCodeGraphRequest { SourceRoot = _source, TargetRoot = Path.Combine(_root, "gone") });

        Assert.Equal("TargetMissing", response.Outcome);
    }

    [Fact]
    public async Task Init_leaves_an_existing_feature_index_alone()
    {
        CreateIndex(_source);
        CreateIndex(_target);
        var commandLine = new ScriptedCommandLine();

        var response = await new InitCodeGraphCommand(CreateCli(commandLine))
            .ExecuteAsync(new InitCodeGraphRequest { SourceRoot = _source, TargetRoot = _target });

        Assert.Equal("AlreadyInitialized", response.Outcome);
        Assert.Empty(commandLine.Calls);
    }

    [Fact]
    public async Task Init_reports_when_codegraph_is_not_installed()
    {
        CreateIndex(_source);

        var response = await new InitCodeGraphCommand(CreateCli(new ScriptedCommandLine(), executable: null))
            .ExecuteAsync(new InitCodeGraphRequest { SourceRoot = _source, TargetRoot = _target });

        Assert.Equal("NotInstalled", response.Outcome);
    }

    [Fact]
    public async Task Init_starts_a_background_build_in_the_feature_root()
    {
        CreateIndex(_source);
        var commandLine = new ScriptedCommandLine { Block = true };
        var cli = CreateCli(commandLine);
        var command = new InitCodeGraphCommand(cli);

        var first = await command.ExecuteAsync(new InitCodeGraphRequest { SourceRoot = _source, TargetRoot = _target });
        var call = await commandLine.NextCallAsync();
        var second = await command.ExecuteAsync(new InitCodeGraphRequest { SourceRoot = _source, TargetRoot = _target });

        Assert.Equal("Started", first.Outcome);
        Assert.Equal("AlreadyRunning", second.Outcome);
        Assert.Equal(Executable, call.FileName);
        Assert.Equal(["init", "-y", _target], call.Arguments);
        Assert.Equal(_target, call.WorkingDirectory);

        Assert.True(await cli.StopInitAsync(_target, CancellationToken.None));
        Assert.False(cli.IsInitRunning(_target));
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
        CreateIndex(_source);
        var commandLine = new ScriptedCommandLine { Block = true };
        var cli = CreateCli(commandLine);
        await new InitCodeGraphCommand(cli).ExecuteAsync(new InitCodeGraphRequest { SourceRoot = _source, TargetRoot = _target });
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
    public async Task Uninit_reports_a_failed_uninit()
    {
        CreateIndex(_target);
        var commandLine = new ScriptedCommandLine { Result = new CommandLineResult(1, "", "database is locked\nmore") };

        var response = await new UninitCodeGraphCommand(CreateCli(commandLine))
            .ExecuteAsync(new UninitCodeGraphRequest { Root = _target });

        Assert.Equal("Failed", response.Outcome);
        Assert.Equal("database is locked", response.Message);
    }

    private static CodeGraphCli CreateCli(ICommandLineService commandLine, string? executable = Executable) =>
        new(commandLine, new StubLifetime(), NullLogger<CodeGraphCli>.Instance) { ExecutableResolver = () => executable };

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
