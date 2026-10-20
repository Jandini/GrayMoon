using GrayMoon.Common;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using GrayMoon.Worker.Services.Ai;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

public sealed class GetHostInfoCommandTests
{
    [Fact]
    public async Task Version_probes_run_together_up_to_the_limit()
    {
        var commandLine = new HoldingCommandLine();
        var codeGraph = new CodeGraphCli(commandLine, new StubLifetime(), NullLogger<CodeGraphCli>.Instance)
        {
            ExecutableResolver = () => "codegraph",
        };
        var command = new GetHostInfoCommand(
            commandLine,
            codeGraph,
            new AiCliRunner(
                [new ClaudeCliAdapter(), new CodexCliAdapter(), new CursorCliAdapter()],
                new MissingLocator(),
                new UnusedRunner(),
                NullLogger<AiCliRunner>.Instance));

        var response = await command.ExecuteAsync(new GetHostInfoRequest(), CancellationToken.None);

        Assert.Equal(AiCliRunner.MaxConcurrentProbes, commandLine.MaxInFlight);
        Assert.Equal("10.0.1", response.DotnetVersion);
        Assert.Equal("2.49.0", response.GitVersion);
        Assert.Equal("5.12.0", response.GitVersionToolVersion);
        Assert.Equal("1.2.3", response.CodeGraphVersion);
        Assert.Null(response.CursorAgentVersion);
    }

    /// <summary>
    /// Each call waits until <see cref="AiCliRunner.MaxConcurrentProbes"/> calls are in flight, which only happens
    /// when the probes overlap. A sequential host check never releases the first call.
    /// </summary>
    private sealed class HoldingCommandLine : ICommandLineService
    {
        private int _inFlight;
        private readonly TaskCompletionSource _allInFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxInFlight { get; private set; }

        public Task<CommandLineResult> RunAsync(
            string fileName,
            string arguments,
            string? workingDirectory = null,
            string? stdin = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null) =>
            HoldAsync(Stdout(fileName, arguments), cancellationToken);

        public Task<CommandLineResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            byte[]? stdinBytes = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null) =>
            HoldAsync("1.2.3", cancellationToken);

        private async Task<CommandLineResult> HoldAsync(string stdout, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (_allInFlight)
            {
                if (now > MaxInFlight)
                    MaxInFlight = now;
            }

            if (now >= AiCliRunner.MaxConcurrentProbes)
                _allInFlight.TrySetResult();

            await _allInFlight.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            Interlocked.Decrement(ref _inFlight);
            return new CommandLineResult(0, stdout, null);
        }

        private static string Stdout(string fileName, string arguments) => (fileName, arguments) switch
        {
            ("dotnet", "--version") => "10.0.1",
            ("git", "--version") => "git version 2.49.0",
            ("dotnet", "gitversion version") => "5.12.0",
            _ => "",
        };
    }

    private sealed class MissingLocator : IAiCliExecutableLocator
    {
        public string? Find(IReadOnlyList<string> names) => null;
    }

    private sealed class UnusedRunner : IBoundedProcessRunner
    {
        public Task<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
