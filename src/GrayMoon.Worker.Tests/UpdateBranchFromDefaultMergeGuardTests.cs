using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Update from default refuses to start while a merge is unfinished. In a Feature the checkout is a linked worktree,
/// where <c>.git</c> is a file and <c>MERGE_HEAD</c> lives in the main repository's <c>worktrees</c> folder, so the
/// guard has to ask git where the file is. Real git.
/// </summary>
public sealed class UpdateBranchFromDefaultMergeGuardTests : IDisposable
{
    private readonly string _root = TestDirectory.Create("merge-guard");
    private readonly UpdateBranchFromDefaultCommand _command;

    public UpdateBranchFromDefaultMergeGuardTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        var git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
        _command = new UpdateBranchFromDefaultCommand(git, reader, runner, NullLogger<UpdateBranchFromDefaultCommand>.Instance);
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Refuses_when_a_merge_is_unfinished_in_a_linked_worktree()
    {
        var main = Path.Combine(_root, "store", "repo");
        GitScenarios.CommittedRepository(main);
        AddOtherBranch(main);
        var worktree = Path.Combine(_root, "ws", "repo");
        GitConfigTestSupport.Git(main, $"worktree add -b feat \"{worktree}\" main");
        GitConfigTestSupport.Git(worktree, "merge --no-ff --no-commit other");
        Assert.True(File.Exists(Path.Combine(worktree, ".git")), "a linked worktree has a .git file");

        var response = await _command.ExecuteAsync(Request());

        Assert.False(response.Success);
        Assert.Contains("merge is already in progress", response.ErrorMessage);
    }

    [Fact]
    public async Task Refuses_when_a_merge_is_unfinished_in_a_normal_clone()
    {
        var repo = Path.Combine(_root, "ws", "repo");
        GitScenarios.CommittedRepository(repo);
        AddOtherBranch(repo);
        GitConfigTestSupport.Git(repo, "merge --no-ff --no-commit other");

        var response = await _command.ExecuteAsync(Request());

        Assert.False(response.Success);
        Assert.Contains("merge is already in progress", response.ErrorMessage);
    }

    [Fact]
    public async Task Does_not_refuse_a_linked_worktree_without_a_merge()
    {
        var main = Path.Combine(_root, "store", "repo");
        GitScenarios.CommittedRepository(main);
        var worktree = Path.Combine(_root, "ws", "repo");
        GitConfigTestSupport.Git(main, $"worktree add -b feat \"{worktree}\" main");

        var response = await _command.ExecuteAsync(Request());

        // There is no origin, so the fetch fails; what matters is that the merge guard let it through.
        Assert.DoesNotContain("merge is already in progress", response.ErrorMessage ?? "");
    }

    private UpdateBranchFromDefaultRequest Request() => new()
    {
        WorkspaceRoot = _root,
        WorkspaceName = "ws",
        RepositoryName = "repo",
        CurrentBranchName = "feat",
        DefaultBranchName = "main",
    };

    /// <summary>A branch <c>other</c> one commit ahead of <c>main</c>; the checkout is left on <c>main</c>.</summary>
    private static void AddOtherBranch(string repo)
    {
        GitConfigTestSupport.Git(repo, "checkout -q -b other");
        File.WriteAllText(Path.Combine(repo, "other.txt"), "other\n");
        GitConfigTestSupport.Git(repo, "add -A");
        GitConfigTestSupport.Git(repo, "commit -q -m other");
        GitConfigTestSupport.Git(repo, "checkout -q main");
    }
}
