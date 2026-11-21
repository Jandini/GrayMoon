using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Create Branch when a local branch of that name is already there. It used to try the create, and after any failure
/// check out the existing branch and answer plain success: the requested base was ignored and the caller was not told,
/// so Prepare Workspace carried on (update, commit, push) on a branch that could be old. It now says the branch already
/// existed, and a create that fails for another reason is a failure. Real git against a local origin.
/// </summary>
public sealed class GitServiceCreateBranchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-create-branch-").FullName;
    private readonly GitService _git;
    private readonly string _repo;
    private readonly string _mainSha;

    public GitServiceCreateBranchTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());

        _repo = GitScenarios.ClonedRepository(_root, push: true);
        _mainSha = Head();
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task A_new_branch_is_created_from_the_base_and_is_not_reported_as_existing()
    {
        var (success, error, alreadyExisted) = await _git.CreateBranchAsync(_repo, "feature/x", "main", CancellationToken.None);

        Assert.True(success, error);
        Assert.False(alreadyExisted);
        Assert.Equal("feature/x", CurrentBranch());
        Assert.Equal(_mainSha, Head());
    }

    [Fact]
    public async Task An_existing_branch_is_checked_out_as_it_is_and_reported_as_already_existing()
    {
        // feature/x stays at the first commit while main moves on, so "created from main" and "the old branch" differ.
        Git("branch feature/x");
        CommitFile("later.md", "later\n");
        var newMainSha = Head();
        Assert.NotEqual(_mainSha, newMainSha);

        var (success, error, alreadyExisted) = await _git.CreateBranchAsync(_repo, "feature/x", "main", CancellationToken.None);

        Assert.True(success, error);
        Assert.True(alreadyExisted);
        Assert.Equal("feature/x", CurrentBranch());
        Assert.Equal(_mainSha, Head());
    }

    [Fact]
    public async Task A_create_that_fails_for_another_reason_is_a_failure_and_leaves_the_checkout_alone()
    {
        var (success, error, alreadyExisted) = await _git.CreateBranchAsync(_repo, "feature/x", "no-such-base", CancellationToken.None);

        Assert.False(success);
        Assert.False(alreadyExisted);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal("main", CurrentBranch());
        Assert.DoesNotContain("feature/x", Git("branch --list feature/x"));
    }

    [Fact]
    public async Task An_existing_branch_that_cannot_be_checked_out_is_a_failure_that_still_says_it_existed()
    {
        // README.md differs between the two branches and has a local change, so git refuses the switch.
        Git("branch feature/x");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "on main\n");
        Git("add -A");
        Git("commit -m change-readme");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "local edit\n");

        var (success, error, alreadyExisted) = await _git.CreateBranchAsync(_repo, "feature/x", "main", CancellationToken.None);

        Assert.False(success);
        Assert.True(alreadyExisted);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal("main", CurrentBranch());
        Assert.Equal("local edit\n", File.ReadAllText(Path.Combine(_repo, "README.md")));
    }

    /// <summary>
    /// A repository without commits: "main" is only the name of the unborn branch, so <c>git checkout -b topic main</c>
    /// fails with "'main' is not a commit". The new branch takes the place of the unborn one and staged work stays.
    /// </summary>
    [Fact]
    public async Task A_repository_without_commits_gets_the_new_branch_as_its_current_branch()
    {
        var root = Directory.CreateTempSubdirectory("graymoon-create-branch-empty-").FullName;
        try
        {
            var empty = GitScenarios.ClonedRepository(root, commit: false);
            File.WriteAllText(Path.Combine(empty, "draft.md"), "draft\n");
            GitConfigTestSupport.Git(empty, "add draft.md");

            var (success, error, alreadyExisted) = await _git.CreateBranchAsync(empty, "feature/x", "main", CancellationToken.None);

            Assert.True(success, error);
            Assert.False(alreadyExisted);
            Assert.Equal("refs/heads/feature/x", GitConfigTestSupport.Git(empty, "symbolic-ref HEAD").Trim());
            Assert.Contains("draft.md", GitConfigTestSupport.Git(empty, "diff --cached --name-only"));

            // Checking out the branch it is on used to fail with "pathspec 'feature/x' did not match"; another name
            // moves the unborn HEAD, so the user can go back.
            var (sameOk, sameError) = await _git.CheckoutBranchAsync(empty, "feature/x", CancellationToken.None);
            Assert.True(sameOk, sameError);
            Assert.Equal("refs/heads/feature/x", GitConfigTestSupport.Git(empty, "symbolic-ref HEAD").Trim());

            var (backOk, backError) = await _git.CheckoutBranchAsync(empty, "main", CancellationToken.None);
            Assert.True(backOk, backError);
            Assert.Equal("refs/heads/main", GitConfigTestSupport.Git(empty, "symbolic-ref HEAD").Trim());
            Assert.Contains("draft.md", GitConfigTestSupport.Git(empty, "diff --cached --name-only"));
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    private void CommitFile(string name, string content)
    {
        File.WriteAllText(Path.Combine(_repo, name), content);
        Git("add -A");
        Git("commit -m add-" + name);
    }

    private string Head() => Git("rev-parse HEAD").Trim();

    private string CurrentBranch() => Git("rev-parse --abbrev-ref HEAD").Trim();

    private string Git(string arguments) => GitConfigTestSupport.Git(_repo, arguments);
}
