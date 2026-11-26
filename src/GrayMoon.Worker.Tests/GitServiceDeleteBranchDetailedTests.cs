using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The detailed result of a local branch delete: the tip and upstream it read, and why a branch was kept. Remove
/// Feature decides its next step from these (a lease on the tip, a look at the pull request) instead of from the
/// text of the error. Real git.
/// </summary>
public sealed class GitServiceDeleteBranchDetailedTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-dbd-").FullName;
    private readonly GitProcessCounter _counter;
    private readonly GitService _git;
    private readonly string _repo;

    public GitServiceDeleteBranchDetailedTests()
    {
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        _counter = new GitProcessCounter(inner);
        var runner = new GitProcessRunner(_counter, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
        _repo = Path.Combine(_root, "repo");
        GitScenarios.CommittedRepository(_repo);
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task A_merged_branch_is_deleted_and_its_tip_is_reported()
    {
        GitConfigTestSupport.Git(_repo, "branch merged");
        var tip = GitConfigTestSupport.Git(_repo, "rev-parse merged").Trim();

        var result = await DeleteAsync("merged");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.FailureKind);
        Assert.Equal(tip, result.BranchSha);
        Assert.False(result.HasUpstream);
        Assert.Equal("", GitConfigTestSupport.Git(_repo, "branch --list merged").Trim());
    }

    [Fact]
    public async Task A_branch_with_commits_that_are_not_merged_is_kept_with_its_tip_as_evidence()
    {
        var tip = CreateUnmergedBranch("work");

        var result = await DeleteAsync("work");

        Assert.False(result.Success);
        Assert.Equal(BranchDeleteFailureKinds.NotFullyMerged, result.FailureKind);
        Assert.Equal(tip, result.BranchSha);
        Assert.Equal(tip, GitConfigTestSupport.Git(_repo, "rev-parse work").Trim());
    }

    [Fact]
    public async Task A_missing_branch_and_the_checked_out_branch_are_told_apart()
    {
        var missing = await DeleteAsync("no-such-branch", force: true);
        var current = await DeleteAsync("main", force: true);

        Assert.Equal(BranchDeleteFailureKinds.NotFound, missing.FailureKind);
        Assert.Null(missing.BranchSha);
        Assert.Null(missing.HasUpstream);
        Assert.Equal(BranchDeleteFailureKinds.CheckedOut, current.FailureKind);
        Assert.NotNull(current.BranchSha);
    }

    [Fact]
    public async Task A_forced_delete_keeps_a_branch_that_moved_since_the_expected_commit()
    {
        // The consent (a reviewed plan, a merged pull request) was for one commit. New commits were never part of it.
        var reviewed = CreateUnmergedBranch("work");
        GitConfigTestSupport.Git(_repo, "checkout --quiet work");
        File.WriteAllText(Path.Combine(_repo, "later.txt"), "added after the review\n");
        GitConfigTestSupport.Git(_repo, "add -A");
        GitConfigTestSupport.Git(_repo, "commit -m later");
        var moved = GitConfigTestSupport.Git(_repo, "rev-parse HEAD").Trim();
        GitConfigTestSupport.Git(_repo, "checkout --quiet main");

        var result = await DeleteAsync("work", force: true, expectedSha: reviewed);

        Assert.False(result.Success);
        Assert.Equal(BranchDeleteFailureKinds.TipChanged, result.FailureKind);
        Assert.Equal(moved, result.BranchSha);
        Assert.Equal(moved, GitConfigTestSupport.Git(_repo, "rev-parse work").Trim());
    }

    [Fact]
    public async Task A_forced_delete_at_the_expected_commit_deletes_with_two_git_processes()
    {
        var tip = CreateUnmergedBranch("work");
        _counter.Clear();

        var result = await DeleteAsync("work", force: true, expectedSha: tip);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(tip, result.BranchSha);
        Assert.True(_counter.Count == 2, $"Expected the ref listing and the delete, saw:{Environment.NewLine}{_counter.Describe()}");
    }

    [Fact]
    public async Task A_branch_with_an_upstream_reports_it()
    {
        var remote = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(remote);
        GitConfigTestSupport.Git(remote, "init --bare --initial-branch=main");
        GitConfigTestSupport.Git(_repo, $"remote add origin \"{remote.Replace('\\', '/')}\"");
        GitConfigTestSupport.Git(_repo, "push --quiet -u origin main");
        GitConfigTestSupport.Git(_repo, "branch pushed");
        GitConfigTestSupport.Git(_repo, "push --quiet -u origin pushed");

        var result = await DeleteAsync("pushed");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.HasUpstream);
    }

    private Task<BranchDeleteResult> DeleteAsync(string branch, bool force = false, string? expectedSha = null)
        => _git.DeleteBranchDetailedAsync(
            _repo, branch, isRemote: false, force, CancellationToken.None, skipHooks: true, expectedSha: expectedSha);

    /// <summary>A branch one commit ahead of main, with main checked out again. Returns the branch tip.</summary>
    private string CreateUnmergedBranch(string name)
    {
        GitConfigTestSupport.Git(_repo, $"checkout --quiet -b {name}");
        File.WriteAllText(Path.Combine(_repo, name + ".txt"), "work\n");
        GitConfigTestSupport.Git(_repo, "add -A");
        GitConfigTestSupport.Git(_repo, $"commit -m {name}");
        var tip = GitConfigTestSupport.Git(_repo, "rev-parse HEAD").Trim();
        GitConfigTestSupport.Git(_repo, "checkout --quiet main");
        return tip;
    }
}
