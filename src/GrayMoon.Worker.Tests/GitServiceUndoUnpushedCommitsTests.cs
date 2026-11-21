using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Undo of outgoing commits on a branch that was never pushed. It used to push the branch first and then reset
/// to the ref the push had just created: the commits stayed, nothing came back to the working tree, and the
/// commits being undone were published. It now resets to where the branch left the ref its outgoing commits are
/// counted against, and never contacts the remote. Real git against a local origin.
/// </summary>
public sealed class GitServiceUndoUnpushedCommitsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-undo-unpushed-").FullName;
    private readonly GitService _git;
    private readonly string _repo;
    private readonly string _origin;
    private readonly string _mainSha;

    public GitServiceUndoUnpushedCommitsTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());

        _repo = GitScenarios.ClonedRepository(_root, push: true);
        _origin = Path.Combine(_root, "origin.git");
        _mainSha = Head();
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Keep_changes_on_a_never_pushed_branch_brings_the_files_back_and_pushes_nothing()
    {
        Git("checkout -b feature/x");
        CommitFile("design.md", "the work\n");

        var (success, error) = await _git.ResetToRemoteAsync(_repo, "feature/x", keepChanges: true, bearerToken: null, CancellationToken.None);

        Assert.True(success, error);
        Assert.Equal(_mainSha, Head());
        Assert.Equal("the work\n", File.ReadAllText(Path.Combine(_repo, "design.md")));
        Assert.Contains("?? design.md", Git("status --porcelain"));
        AssertBranchWasNotPublished("feature/x");
    }

    [Fact]
    public async Task Discard_on_a_never_pushed_branch_removes_the_commit_and_pushes_nothing()
    {
        Git("checkout -b feature/x");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "changed\n");
        Git("add -A");
        Git("commit -m change");

        var (success, error) = await _git.ResetToRemoteAsync(_repo, "feature/x", keepChanges: false, bearerToken: null, CancellationToken.None);

        Assert.True(success, error);
        Assert.Equal(_mainSha, Head());
        Assert.Equal("", Git("status --porcelain").Trim());
        AssertBranchWasNotPublished("feature/x");
    }

    /// <summary>A Feature counts its outgoing commits against its local parent, so the undo stops at the parent's tip.</summary>
    [Fact]
    public async Task A_feature_branch_is_undone_to_its_divergence_base_not_to_the_default_branch()
    {
        Git("checkout -b parent");
        CommitFile("parent.txt", "parent work\n");
        var parentSha = Head();
        Git("checkout -b feature/x");
        await _git.SetDivergenceBaseBranchAsync(_repo, "parent", CancellationToken.None);
        CommitFile("feature.txt", "feature work\n");

        var (success, error) = await _git.ResetToRemoteAsync(_repo, "feature/x", keepChanges: true, bearerToken: null, CancellationToken.None);

        Assert.True(success, error);
        Assert.Equal(parentSha, Head());
        Assert.Contains("?? feature.txt", Git("status --porcelain"));
        Assert.DoesNotContain("parent.txt", Git("status --porcelain"));
        AssertBranchWasNotPublished("feature/x");
    }

    [Fact]
    public async Task A_pushed_branch_is_still_undone_to_its_origin_ref()
    {
        Git("checkout -b feature/x");
        CommitFile("pushed.txt", "pushed\n");
        Git("push -u origin feature/x");
        var pushedSha = Head();
        CommitFile("local.txt", "local\n");

        var (success, error) = await _git.ResetToRemoteAsync(_repo, "feature/x", keepChanges: true, bearerToken: null, CancellationToken.None);

        Assert.True(success, error);
        Assert.Equal(pushedSha, Head());
        Assert.Contains("?? local.txt", Git("status --porcelain"));
        Assert.Equal(pushedSha, GitConfigTestSupport.Git(_origin, "rev-parse refs/heads/feature/x").Trim());
    }

    private void AssertBranchWasNotPublished(string branch)
    {
        Assert.Equal("", GitConfigTestSupport.Git(_origin, $"for-each-ref refs/heads/{branch}").Trim());
        Assert.Equal("", Git($"for-each-ref refs/remotes/origin/{branch}").Trim());
    }

    private void CommitFile(string name, string content)
    {
        File.WriteAllText(Path.Combine(_repo, name), content);
        Git("add -A");
        Git($"commit -m \"add {name}\"");
    }

    private string Head() => Git("rev-parse HEAD").Trim();

    private string Git(string args) => GitConfigTestSupport.Git(_repo, args);
}
