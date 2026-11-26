using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// How many git processes Remove Feature starts per repository. On the test machine of 2026-11-25 a git process cost
/// about 1.5 s, an inspection of one worktree ran 13 of them (16 to 26 s), and a removal of 40 repositories about 680.
/// </summary>
public sealed class FeatureWorktreeGitProcessCountTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-fwpc-").FullName;
    private readonly RecordingCommandLine _recorder;
    private readonly GitCliRepositoryReader _reader;
    private readonly GitWorktreeService _worktrees;
    private readonly GitService _git;
    private readonly CreateGitWorktreeCommand _create;
    private readonly InspectWorktreeCommand _inspect;

    public FeatureWorktreeGitProcessCountTests()
    {
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        _recorder = new RecordingCommandLine(inner);
        var runner = new GitProcessRunner(_recorder, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _worktrees = new GitWorktreeService(runner, _reader, NullLogger<GitWorktreeService>.Instance, new RepositoryAccess(NullLogger<RepositoryAccess>.Instance));
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
        _create = new CreateGitWorktreeCommand(_git, _worktrees);
        _inspect = new InspectWorktreeCommand(_worktrees);
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Inspecting_a_worktree_on_its_Feature_branch_runs_five_git_processes()
    {
        var (mainPath, worktreePath, head) = await CreateFeatureWorktreeAsync("inspect", "feat-inspect");
        _recorder.Clear();

        var result = await _inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            DefaultBranch = "main",
            FeatureBranch = "feat-inspect",
        });

        Assert.Null(result.Error);
        Assert.Equal(head, result.HeadSha);
        Assert.Equal("feat-inspect", result.Branch);
        Assert.False(result.IsDirty);
        Assert.False(result.HasUpstream);
        Assert.Equal(0, result.AheadOfDefault);
        Assert.True(result.FeatureBranchExists);
        Assert.Equal(head, result.FeatureBranchSha);
        Assert.Equal(0, result.FeatureBranchAheadOfDefault);
        Assert.False(result.FeatureBranchHasUpstream);
        Assert.False(result.HasOperationInProgress);
        // worktree list, status, the divergence base ref, rev-list against it, branch -r --contains HEAD.
        Assert.Equal(5, _recorder.Calls.Count);
    }

    [Fact]
    public async Task Inspecting_reports_changes_commits_and_an_unfinished_operation_from_the_same_status_read()
    {
        var (mainPath, worktreePath, _) = await CreateFeatureWorktreeAsync("dirty", "feat-dirty");
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "committed.txt"), "c\n");
        await RunGitAsync(worktreePath, "add committed.txt");
        await RunGitAsync(worktreePath, "commit -m work");
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "README.md"), "changed\n");
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "staged.txt"), "s\n");
        await RunGitAsync(worktreePath, "add staged.txt");
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "untracked.txt"), "u\n");
        var gitDir = GitDirectoryLocator.TryReadGitDirFromWorkTree(worktreePath)!;
        await File.WriteAllTextAsync(Path.Combine(gitDir, "CHERRY_PICK_HEAD"), "0000000000000000000000000000000000000000\n");

        var result = await _inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            DefaultBranch = "main",
            FeatureBranch = "feat-dirty",
        });

        Assert.Null(result.Error);
        Assert.True(result.IsDirty);
        Assert.Equal(1, result.StagedCount);
        Assert.Equal(1, result.UnstagedCount);
        Assert.Equal(1, result.UntrackedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(1, result.AheadOfDefault);
        Assert.Equal(1, result.FeatureBranchAheadOfDefault);
        Assert.True(result.HasOperationInProgress);
    }

    [Fact]
    public async Task Removing_a_clean_worktree_lists_the_worktrees_once()
    {
        var (mainPath, worktreePath, _) = await CreateFeatureWorktreeAsync("remove", "feat-remove");
        _recorder.Clear();

        var (success, alreadyRemoved, _, error, _) = await _worktrees.RemoveWorktreeAsync(
            mainPath, worktreePath, force: false, CancellationToken.None);

        Assert.True(success, error);
        Assert.False(alreadyRemoved);
        Assert.False(Directory.Exists(worktreePath));
        Assert.Single(_recorder.Calls, c => c.Contains("worktree list", StringComparison.Ordinal));
        Assert.Single(_recorder.Calls, c => c.Contains("worktree remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removing_a_worktree_with_uncommitted_work_without_force_is_refused_and_keeps_the_files()
    {
        var (mainPath, worktreePath, _) = await CreateFeatureWorktreeAsync("keep", "feat-keep");
        var work = Path.Combine(worktreePath, "work.txt");
        await File.WriteAllTextAsync(work, "not committed\n");

        var (success, _, errorCode, error, _) = await _worktrees.RemoveWorktreeAsync(
            mainPath, worktreePath, force: false, CancellationToken.None);

        Assert.False(success);
        Assert.True(File.Exists(work));
        Assert.Equal(WorktreeRemovalFailureKind.UncommittedChanges, WorktreeRemovalFailureClassifier.Classify(errorCode, error));
    }

    [Fact]
    public async Task Force_deleting_a_local_branch_runs_two_git_processes()
    {
        var mainPath = Path.Combine(_root, "branches");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        await RunGitAsync(mainPath, "branch team/login");
        _recorder.Clear();

        var (success, error) = await _git.DeleteBranchAsync(mainPath, "team/login", isRemote: false, force: true, CancellationToken.None, skipHooks: true);

        Assert.True(success, error);
        Assert.Equal(2, _recorder.Calls.Count);
    }

    [Fact]
    public async Task Deleting_a_local_branch_still_tells_a_missing_branch_from_the_checked_out_one()
    {
        var mainPath = Path.Combine(_root, "branches2");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        await RunGitAsync(mainPath, "branch team/login");

        // "team" is only the folder of "team/login", not a branch.
        var missing = await _git.DeleteBranchAsync(mainPath, "team", isRemote: false, force: true, CancellationToken.None);
        var current = await _git.DeleteBranchAsync(mainPath, "main", isRemote: false, force: true, CancellationToken.None);

        Assert.Equal((false, "Branch does not exist."), missing);
        Assert.Equal((false, "Cannot delete the current branch. Check out another branch first."), current);
    }

    private async Task<(string MainPath, string WorktreePath, string Head)> CreateFeatureWorktreeAsync(string name, string branch)
    {
        var mainPath = Path.Combine(_root, name);
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = (await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None))!;
        var worktreePath = Path.Combine(_root, "features", name, "repo");
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = branch,
            BaseCommitSha = head,
            DivergenceBaseBranch = "main",
        });
        Assert.True(created.Success, created.ErrorMessage);
        return (mainPath, worktreePath, head);
    }

    private static async Task InitGitWithCommitAsync(string repoPath)
    {
        await RunGitAsync(repoPath, "init -b main");
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

    private sealed class RecordingCommandLine(ICommandLineService inner) : ICommandLineService
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _calls = new();

        public IReadOnlyCollection<string> Calls => _calls;

        public void Clear() => _calls.Clear();

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
            _calls.Enqueue(arguments);
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
            _calls.Enqueue(string.Join(' ', arguments));
            return inner.RunAsync(fileName, arguments, workingDirectory, stdin, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout);
        }
    }
}
