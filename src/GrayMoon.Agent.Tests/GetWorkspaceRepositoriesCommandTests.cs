using GrayMoon.Agent;
using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Tests;

public sealed class GetWorkspaceRepositoriesCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-ws-repos-").FullName;
    private readonly RecordingCommandLineService _recorder;
    private readonly GetWorkspaceRepositoriesCommand _command;

    public GetWorkspaceRepositoriesCommandTests()
    {
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        _recorder = new RecordingCommandLineService(inner);
        var runner = new GitProcessRunner(_recorder, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var git = new GitService(Options.Create(new AgentOptions()), NullLogger<GitService>.Instance, runner);
        _command = new GetWorkspaceRepositoriesCommand(git);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task Skips_child_folder_without_git_and_does_not_probe_origin()
    {
        var workspaceName = "ws";
        var workspacePath = Path.Combine(_root, workspaceName);
        var gitRepo = Path.Combine(workspacePath, "AVR");
        var workDir = Path.Combine(workspacePath, ".work");
        Directory.CreateDirectory(gitRepo);
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(Path.Combine(gitRepo, ".git"));
        File.WriteAllText(Path.Combine(workDir, "scratch.txt"), "cursor\n");

        var response = await _command.ExecuteAsync(new GetWorkspaceRepositoriesRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = workspaceName,
        });

        Assert.Contains("AVR", response.Repositories);
        Assert.DoesNotContain(".work", response.Repositories);
        Assert.DoesNotContain(_recorder.Calls, c =>
            c.WorkingDirectory != null
            && c.WorkingDirectory.Replace('\\', '/').TrimEnd('/').EndsWith("/.work", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingCommandLineService(ICommandLineService inner) : ICommandLineService
    {
        public List<RecordedCall> Calls { get; } = [];

        public Task<CommandLineResult> RunAsync(
            string fileName,
            string arguments,
            string? workingDirectory = null,
            string? stdin = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null)
        {
            Calls.Add(new RecordedCall(fileName, arguments, workingDirectory));
            return inner.RunAsync(fileName, arguments, workingDirectory, stdin, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout);
        }

        public Task<CommandLineResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            byte[]? stdinBytes = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null)
        {
            Calls.Add(new RecordedCall(fileName, string.Join(' ', arguments), workingDirectory));
            return inner.RunAsync(fileName, arguments, workingDirectory, stdinBytes, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout);
        }

        public sealed record RecordedCall(string FileName, string? Arguments, string? WorkingDirectory);
    }
}
