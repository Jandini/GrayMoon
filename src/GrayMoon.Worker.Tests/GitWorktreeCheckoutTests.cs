using System.Collections.Concurrent;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// A new Feature worktree is registered with <c>git worktree add --no-checkout</c> and filled by
/// <c>git checkout --progress</c>, whose progress output keeps it alive on the clone tier. Before this, the add wrote the
/// files itself in silence under the 60 s default timeout, so a repository whose checkout takes longer could never get a
/// Feature worktree.
/// </summary>
public sealed class GitWorktreeCheckoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-wt-checkout-").FullName;
    private readonly RecordingCommandLine _commandLine = new(new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions())));
    private readonly GitCliRepositoryReader _reader;
    private readonly GitWorktreeService _worktrees;

    public GitWorktreeCheckoutTests()
    {
        var runner = new GitProcessRunner(_commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _worktrees = new GitWorktreeService(runner, _reader, NullLogger<GitWorktreeService>.Instance,
            new RepositoryAccess(NullLogger<RepositoryAccess>.Instance));
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Create_fills_the_worktree_with_a_progress_checkout_bounded_only_by_inactivity()
    {
        var mainPath = Path.Combine(_root, "main");
        GitScenarios.CommittedRepository(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);
        var worktreePath = Path.Combine(_root, "features", "ABC-1", "main");

        var created = await _worktrees.CreateWorktreeAsync(mainPath, worktreePath, "ABC-1", head!, CancellationToken.None);

        Assert.True(created.Success, created.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(worktreePath, "README.md")));

        var add = Assert.Single(_commandLine.Calls, c => c.Arguments is ["worktree", "add", ..]);
        Assert.Contains("--no-checkout", add.Arguments);

        var checkout = Assert.Single(_commandLine.Calls, c => c.Arguments is ["checkout", ..]);
        Assert.Equal(["checkout", "--progress", "--force"], checkout.Arguments);
        Assert.Equal(Path.GetFullPath(worktreePath), checkout.WorkingDirectory);
        Assert.Equal(Timeout.InfiniteTimeSpan, checkout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(180), checkout.IdleTimeout);

        var status = await _commandLine.RunAsync("git", ["status", "--porcelain"], worktreePath);
        Assert.Equal(0, status.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(status.Stdout), status.Stdout);
    }

    [Fact]
    public async Task Failed_checkout_removes_the_unfinished_worktree_and_its_new_branch_but_nothing_else()
    {
        var mainPath = Path.Combine(_root, "main");
        GitScenarios.CommittedRepository(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);
        // Data the cleanup must not touch: another branch, and a worktree of an earlier Feature.
        Assert.Equal(0, (await _commandLine.RunAsync("git", ["branch", "keep"], mainPath)).ExitCode);
        var otherWorktree = Path.Combine(_root, "features", "OLD-1", "main");
        Assert.True((await _worktrees.CreateWorktreeAsync(mainPath, otherWorktree, "OLD-1", head!, CancellationToken.None)).Success);

        _commandLine.FailCheckout = true;
        var worktreePath = Path.Combine(_root, "features", "ABC-1", "main");
        var failed = await _worktrees.CreateWorktreeAsync(mainPath, worktreePath, "ABC-1", head!, CancellationToken.None);

        Assert.False(failed.Success);
        Assert.Equal("CheckoutFailed", failed.ErrorCode);
        Assert.Contains("simulated checkout failure", failed.ErrorMessage);
        Assert.DoesNotContain("removing the unfinished worktree failed", failed.ErrorMessage);

        var (listOk, worktrees, _, _) = await _worktrees.ListWorktreesAsync(mainPath, CancellationToken.None);
        Assert.True(listOk);
        Assert.Null(GitWorktreeOccupancy.FindByPath(worktrees, Path.GetFullPath(worktreePath)));
        Assert.NotNull(GitWorktreeOccupancy.FindByPath(worktrees, Path.GetFullPath(otherWorktree)));
        Assert.False(Directory.Exists(worktreePath));
        Assert.NotEqual(0, (await _commandLine.RunAsync("git", ["rev-parse", "--verify", "--quiet", "refs/heads/ABC-1"], mainPath)).ExitCode);
        Assert.Equal(0, (await _commandLine.RunAsync("git", ["rev-parse", "--verify", "--quiet", "refs/heads/keep"], mainPath)).ExitCode);
        Assert.True(File.Exists(Path.Combine(otherWorktree, "README.md")));
        Assert.True(File.Exists(Path.Combine(mainPath, "README.md")));

        // Nothing is left that would block or fool a retry.
        _commandLine.FailCheckout = false;
        var retried = await _worktrees.CreateWorktreeAsync(mainPath, worktreePath, "ABC-1", head!, CancellationToken.None);
        Assert.True(retried.Success, retried.ErrorMessage);
        Assert.False(retried.AlreadyExisted);
        Assert.True(File.Exists(Path.Combine(worktreePath, "README.md")));
    }

    [Fact]
    public async Task Failed_checkout_of_a_detached_worktree_removes_the_worktree()
    {
        var mainPath = Path.Combine(_root, "main");
        GitScenarios.CommittedRepository(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);

        _commandLine.FailCheckout = true;
        var worktreePath = Path.Combine(_root, "features", "ABC-1", "main");
        var failed = await _worktrees.CreateWorktreeAsync(mainPath, worktreePath, null, head!, CancellationToken.None);

        Assert.Equal("CheckoutFailed", failed.ErrorCode);
        var (_, worktrees, _, _) = await _worktrees.ListWorktreesAsync(mainPath, CancellationToken.None);
        Assert.Null(GitWorktreeOccupancy.FindByPath(worktrees, Path.GetFullPath(worktreePath)));
        Assert.True(File.Exists(Path.Combine(mainPath, "README.md")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ABC-1")]
    public async Task Repair_recreates_a_worktree_that_a_killed_add_left_unfinished(string? branchName)
    {
        var mainPath = Path.Combine(_root, "main");
        GitScenarios.CommittedRepository(mainPath);
        var head = (await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None))!;
        var worktreePath = Path.Combine(_root, "features", "ABC-1", "main");
        await LeaveUnfinishedWorktreeAsync(mainPath, worktreePath, branchName, head);

        var repaired = await _worktrees.CreateWorktreeAsync(mainPath, worktreePath, branchName, head, CancellationToken.None);

        Assert.True(repaired.Success, repaired.ErrorMessage);
        Assert.False(repaired.AlreadyExisted);
        Assert.True(File.Exists(Path.Combine(worktreePath, "README.md")));
        Assert.False(File.Exists(Path.Combine(worktreePath, "partial.tmp")));
        var status = await _commandLine.RunAsync("git", ["status", "--porcelain"], worktreePath);
        Assert.Equal(0, status.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(status.Stdout), status.Stdout);
        Assert.True(File.Exists(Path.Combine(mainPath, "README.md")));
    }

    [Fact]
    public async Task A_finished_worktree_is_still_reported_as_existing_and_left_alone()
    {
        var mainPath = Path.Combine(_root, "main");
        GitScenarios.CommittedRepository(mainPath);
        var head = (await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None))!;
        var worktreePath = Path.Combine(_root, "features", "ABC-1", "main");
        Assert.True((await _worktrees.CreateWorktreeAsync(mainPath, worktreePath, "ABC-1", head, CancellationToken.None)).Success);
        File.WriteAllText(Path.Combine(worktreePath, "work.txt"), "user work");

        var again = await _worktrees.CreateWorktreeAsync(mainPath, worktreePath, "ABC-1", head, CancellationToken.None);

        Assert.True(again.Success);
        Assert.True(again.AlreadyExisted);
        Assert.Equal("user work", File.ReadAllText(Path.Combine(worktreePath, "work.txt")));
    }

    [Fact]
    public async Task Unfinished_worktree_whose_branch_has_moved_keeps_the_branch_and_reports_it()
    {
        var mainPath = Path.Combine(_root, "main");
        GitScenarios.CommittedRepository(mainPath);
        var head = (await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None))!;
        var worktreePath = Path.Combine(_root, "features", "ABC-1", "main");
        await LeaveUnfinishedWorktreeAsync(mainPath, worktreePath, "ABC-1", head);
        var moved = (await _commandLine.RunAsync("git", ["commit-tree", "-p", head, "-m", "work", $"{head}^{{tree}}"], mainPath)).Stdout!.Trim();
        Assert.Equal(0, (await _commandLine.RunAsync("git", ["update-ref", "refs/heads/ABC-1", moved], mainPath)).ExitCode);

        var repaired = await _worktrees.CreateWorktreeAsync(mainPath, worktreePath, "ABC-1", head, CancellationToken.None);

        Assert.False(repaired.Success);
        Assert.Equal("WorktreeUnfinished", repaired.ErrorCode);
        Assert.Contains("has moved", repaired.ErrorMessage);
        var tip = (await _commandLine.RunAsync("git", ["rev-parse", "refs/heads/ABC-1"], mainPath)).Stdout!.Trim();
        Assert.Equal(moved, tip);
    }

    /// <summary>
    /// What a <c>git worktree add</c> killed by the old 60 s timeout leaves behind (seen in a user's logs): the worktree
    /// is registered and locked with reason "initializing", its index was never written, a stale <c>index.lock</c>
    /// remains, and only some files exist.
    /// </summary>
    private async Task LeaveUnfinishedWorktreeAsync(string mainPath, string worktreePath, string? branchName, string head)
    {
        string[] add = branchName is null
            ? ["worktree", "add", "--no-checkout", "--detach", "--", worktreePath, head]
            : ["worktree", "add", "--no-checkout", "-b", branchName, "--", worktreePath, head];
        Assert.Equal(0, (await _commandLine.RunAsync("git", add, mainPath)).ExitCode);
        var gitDir = (await _commandLine.RunAsync("git", ["rev-parse", "--absolute-git-dir"], worktreePath)).Stdout!.Trim();
        File.WriteAllText(Path.Combine(gitDir, "locked"), "initializing");
        File.WriteAllText(Path.Combine(gitDir, "index.lock"), "");
        File.WriteAllText(Path.Combine(worktreePath, "partial.tmp"), "half written");
        Assert.False(File.Exists(Path.Combine(gitDir, "index")));
    }

    private sealed record Call(IReadOnlyList<string> Arguments, string? WorkingDirectory, TimeSpan? Timeout, TimeSpan? IdleTimeout);

    /// <summary>Runs real git, records each argument-list call, and can fail the worktree checkout on demand.</summary>
    private sealed class RecordingCommandLine(ICommandLineService inner) : ICommandLineService
    {
        private readonly ConcurrentQueue<Call> _calls = new();

        public bool FailCheckout { get; set; }

        public IReadOnlyList<Call> Calls => _calls.ToArray();

        public Task<CommandLineResult> RunAsync(string fileName, string arguments, string? workingDirectory = null, string? stdin = null,
            CancellationToken cancellationToken = default, bool streamStderrAsStdout = false, bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null, TimeSpan? idleTimeout = null)
            => inner.RunAsync(fileName, arguments, workingDirectory, stdin, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout, idleTimeout);

        public Task<CommandLineResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, byte[]? stdinBytes = null,
            CancellationToken cancellationToken = default, bool streamStderrAsStdout = false, bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null, TimeSpan? idleTimeout = null)
        {
            _calls.Enqueue(new Call(arguments.ToArray(), workingDirectory, timeout, idleTimeout));
            if (FailCheckout && arguments is ["checkout", ..])
                return Task.FromResult(new CommandLineResult(1, "", "simulated checkout failure"));
            return inner.RunAsync(fileName, arguments, workingDirectory, stdinBytes, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout, idleTimeout);
        }
    }
}
