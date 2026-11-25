using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace GrayMoon.Worker.Tests;
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
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        var git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
        _command = new GetHeadCommitsCommand(reader, NullLogger<GetHeadCommitsCommand>.Instance);
    }
    public void Dispose()
    {
        TestDirectory.Delete(_root);
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
        // One git process per repository on a branch: a process costs 1.4 s on the test machine of 2026-11-25, and
        // four per repository made Create Feature wait 51 s for 40 repositories.
        Assert.Equal(2, _recorder.Calls.Count);
    }
    [Fact]
    public async Task Reads_branch_tag_and_collisions_with_at_most_three_git_processes_per_repository()
    {
        var workspacePath = Path.Combine(_root, "ws");
        var onBranch = Path.Combine(workspacePath, "OnBranch");
        var onTag = Path.Combine(workspacePath, "OnTag");
        var detached = Path.Combine(workspacePath, "Detached");
        foreach (var repoPath in new[] { onBranch, onTag, detached })
        {
            Directory.CreateDirectory(repoPath);
            await InitGitWithCommitAsync(repoPath);
        }
        await RunGitAsync(onBranch, "checkout -b work");
        await RunGitAsync(onBranch, "branch feature-x");
        await RunGitAsync(onTag, "tag v1.2.3");
        await RunGitAsync(onTag, "checkout --detach v1.2.3");
        await RunGitAsync(detached, "checkout --detach");

        var response = await _command.ExecuteAsync(new GetHeadCommitsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryNames = ["OnBranch", "OnTag", "Detached"],
            CollisionBranchName = "feature-x",
        });

        Assert.Equal(3, response.Commits!.Count);
        Assert.Equal(KeyValuePair.Create("OnBranch", "work"), Assert.Single(response.Branches!));
        Assert.Equal(KeyValuePair.Create("OnTag", "v1.2.3"), Assert.Single(response.Tags!));
        Assert.Equal(["feature-x"], response.BranchCollisions!["OnBranch"]);
        Assert.False(response.BranchCollisions.ContainsKey("OnTag"));
        // OnBranch: HEAD + collisions. OnTag: HEAD + describe. Detached: HEAD + describe + collisions.
        Assert.Equal(7, _recorder.Calls.Count);
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
        private readonly System.Collections.Concurrent.ConcurrentQueue<RecordedCall> _calls = new();
        public IReadOnlyCollection<RecordedCall> Calls => _calls;
        public Task<CommandLineResult> RunAsync(
            string fileName,
            string arguments,
            string? workingDirectory = null,
            string? stdin = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null, TimeSpan? idleTimeout = null)
        {
            _calls.Enqueue(new RecordedCall(fileName, arguments, workingDirectory));
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
            TimeSpan? timeout = null, TimeSpan? idleTimeout = null)
        {
            _calls.Enqueue(new RecordedCall(fileName, string.Join(' ', arguments), workingDirectory));
            return inner.RunAsync(fileName, arguments, workingDirectory, stdin, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout);
        }
    }
    private sealed record RecordedCall(string FileName, string Arguments, string? WorkingDirectory);
}
