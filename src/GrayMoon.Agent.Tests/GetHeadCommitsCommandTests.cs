using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace GrayMoon.Agent.Tests;
public sealed class GetHeadCommitsCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-ghc-").FullName;
    private readonly GetHeadCommitsCommand _command;
    private readonly RecordingCommandLineService _recorder;
    public GetHeadCommitsCommandTests()
    {
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        _recorder = new RecordingCommandLineService(inner);
        var runner = new GitProcessRunner(_recorder, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var git = new GitService(Options.Create(new AgentOptions()), NullLogger<GitService>.Instance, runner);
        _command = new GetHeadCommitsCommand(git, NullLogger<GetHeadCommitsCommand>.Instance);
    }
    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }
    [Fact]
    public async Task Resolves_head_once_per_unique_repository()
    {
        var workspaceName = "ws";
        var workspacePath = Path.Combine(_root, workspaceName);
        foreach (var repo in new[] { "RepoA", "RepoB" })
        {
            var repoPath = Path.Combine(workspacePath, repo);
            Directory.CreateDirectory(repoPath);
            await InitGitWithCommitAsync(repoPath);
        }
        var response = await _command.ExecuteAsync(new GetHeadCommitsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = workspaceName,
            RepositoryNames = ["RepoA", "RepoA", "RepoB"]
        });
        Assert.NotNull(response.Commits);
        Assert.Equal(2, response.Commits!.Count);
        Assert.True(response.Commits.ContainsKey("RepoA"));
        Assert.True(response.Commits.ContainsKey("RepoB"));
        Assert.Equal(40, response.Commits["RepoA"].Length);
        Assert.NotNull(response.Branches);
        Assert.True(response.Branches!.ContainsKey("RepoA"));
        Assert.False(string.IsNullOrWhiteSpace(response.Branches["RepoA"]));
        var revParseCalls = _recorder.Calls.Count(c =>
            c.Arguments.Contains("rev-parse", StringComparison.OrdinalIgnoreCase)
            && c.Arguments.Contains("HEAD", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, revParseCalls);
        var branchShowCalls = _recorder.Calls.Count(c =>
            c.Arguments.Contains("branch --show-current", StringComparison.OrdinalIgnoreCase)
            || (c.Arguments.Contains("branch", StringComparison.OrdinalIgnoreCase)
                && c.Arguments.Contains("--show-current", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2, branchShowCalls);
    }
    [Fact]
    public async Task Unborn_repository_omits_commit()
    {
        var workspaceName = "ws";
        var repoName = "Empty";
        var repoPath = Path.Combine(_root, workspaceName, repoName);
        Directory.CreateDirectory(repoPath);
        await RunGitAsync(repoPath, "init");
        var response = await _command.ExecuteAsync(new GetHeadCommitsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = workspaceName,
            RepositoryNames = [repoName]
        });
        Assert.NotNull(response.Commits);
        Assert.False(response.Commits!.ContainsKey(repoName));
    }
    private static async Task InitGitWithCommitAsync(string repoPath)
    {
        await RunGitAsync(repoPath, "init");
        await RunGitAsync(repoPath, "config user.email test@example.com");
        await RunGitAsync(repoPath, "config user.name Test");
        await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "x\n");
        await RunGitAsync(repoPath, "add README.md");
        await RunGitAsync(repoPath, "commit -m init");
    }
    private static async Task RunGitAsync(string repoPath, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed: {await p.StandardError.ReadToEndAsync()}");
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
            byte[]? stdin = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null)
        {
            Calls.Add(new RecordedCall(fileName, string.Join(' ', arguments), workingDirectory));
            return inner.RunAsync(fileName, arguments, workingDirectory, stdin, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout);
        }
    }
    private sealed record RecordedCall(string FileName, string Arguments, string? WorkingDirectory);
}
