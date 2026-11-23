using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The parent branch version at a branch point (effective versions, design section 5.3). Real git, real GitVersion,
/// from a checkout on <c>feature/abc</c> that sits on the same commit as <c>release/2.4</c>. The first tests record
/// what GitVersion does with the plain invocations, which is why the command uses <c>/b</c>, <c>/c</c> and
/// <c>/nocache</c> together and checks the branch and SHA of the output.
/// </summary>
[Trait("Category", "PullRequest")]
public sealed class GitVersionAtBranchPointTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-branch-point-").FullName;
    private readonly GitService _git;
    private readonly GetGitVersionAtBranchPointCommand _command;
    private readonly string _repo;
    private readonly string _baseline;

    public GitVersionAtBranchPointTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
        _command = new GetGitVersionAtBranchPointCommand(new GitVersionRepositoryVersionProvider(_git), runner, NullLogger<GetGitVersionAtBranchPointCommand>.Instance);

        _repo = GitScenarios.BranchPointRepository(_root);
        _baseline = Git(_repo, "rev-parse HEAD");
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [FactIfGitVersion]
    public async Task Commit_alone_versions_the_checked_out_branch_not_the_parent()
    {
        var (result, error) = await _git.GetVersionAsync(_repo, nonNormalize: true, commitSha: _baseline, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("feature/abc", result!.BranchName);
    }

    [FactIfGitVersion]
    public async Task Branch_and_commit_without_nocache_return_the_cached_result_for_HEAD()
    {
        var (exitHead, _, _) = await GitVersionParityTests.RunProcessAsync("dotnet-gitversion", "/output json /nofetch /verbosity quiet /nonormalize", _repo);
        Assert.Equal(0, exitHead);

        var (exit, stdout, stderr) = await GitVersionParityTests.RunProcessAsync(
            "dotnet-gitversion", $"/output json /nofetch /verbosity quiet /nonormalize /b release/2.4 /c {_baseline}", _repo);

        Assert.True(exit == 0, stderr);
        var cached = System.Text.Json.JsonSerializer.Deserialize<GitVersionResult>(stdout);
        Assert.Equal("feature/abc", cached!.BranchName);
    }

    [FactIfGitVersion]
    public async Task The_parent_version_at_the_branch_point_is_the_release_branch_version_and_the_checkout_is_untouched()
    {
        var statusBefore = Git(_repo, "status --porcelain=v2 --branch");

        var response = await RunAsync("release/2.4", _baseline);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.True(response.BaselineOnRemoteParent);
        Assert.StartsWith("2.4.0-", response.Version);
        Assert.Contains("Branch.release-2.4.Sha." + _baseline, response.Version);
        Assert.Equal("feature/abc", Git(_repo, "branch --show-current"));
        Assert.Equal(_baseline, Git(_repo, "rev-parse HEAD"));
        Assert.Equal(statusBefore, Git(_repo, "status --porcelain=v2 --branch"));
    }

    [FactIfGitVersion]
    public async Task The_parent_version_does_not_change_when_the_parent_advances()
    {
        var before = await RunAsync("release/2.4", _baseline);

        Git(_repo, "checkout -q release/2.4");
        File.WriteAllText(Path.Combine(_repo, "c.txt"), "c\n");
        Git(_repo, "add -A");
        Git(_repo, "commit -q -m third");
        Git(_repo, "push -q origin release/2.4");
        Git(_repo, "checkout -q feature/abc");

        var after = await RunAsync("release/2.4", _baseline);

        Assert.True(before.Success, before.ErrorMessage);
        Assert.True(after.Success, after.ErrorMessage);
        Assert.Equal(before.Version, after.Version);
    }

    [FactIfGitVersion]
    public async Task A_parent_that_exists_only_on_origin_gives_no_version()
    {
        // GitVersion then versions "origin/release/2.4", whose InformationalVersion names another branch than the one
        // the CI of the parent published. Recorded outcome: no parent version, so the repository uses its own (D9).
        Git(_repo, "branch -q -D release/2.4");

        var response = await RunAsync("release/2.4", _baseline);

        Assert.False(response.Success);
        Assert.True(response.BaselineOnRemoteParent);
        Assert.Null(response.Version);
        Assert.Contains("origin/release/2.4", response.ErrorMessage);
    }

    [FactIfGitVersion]
    public async Task A_baseline_that_is_not_on_the_remote_parent_is_reported_without_a_version()
    {
        File.WriteAllText(Path.Combine(_repo, "local.txt"), "local\n");
        Git(_repo, "add -A");
        Git(_repo, "commit -q -m local");
        var unpushed = Git(_repo, "rev-parse HEAD");

        var response = await RunAsync("release/2.4", unpushed);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.False(response.BaselineOnRemoteParent);
        Assert.Null(response.Version);
    }

    [FactIfGitVersion]
    public async Task A_Feature_worktree_gets_the_parent_version_and_keeps_its_checkout()
    {
        var worktree = Path.Combine(_root, "features", "repo");
        Git(_repo, $"worktree add -q -b feature/wt \"{worktree}\" {_baseline}");

        var response = await _command.ExecuteAsync(new GetGitVersionAtBranchPointRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "features",
            RepositoryName = "repo",
            ParentBranch = "release/2.4",
            CommitSha = _baseline,
        });

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Contains("Branch.release-2.4.Sha." + _baseline, response.Version);
        Assert.Equal("feature/wt", Git(worktree, "branch --show-current"));
        Assert.Equal("", Git(worktree, "status --porcelain"));
    }

    [Theory]
    [InlineData("-x", null)]
    [InlineData("release 2.4", null)]
    [InlineData("release/../main", null)]
    [InlineData("release/2.4", "abc")]
    [InlineData("release/2.4", "--output=x")]
    public async Task Bad_input_is_refused_before_any_process(string parent, string? sha)
    {
        var response = await RunAsync(parent, sha ?? _baseline);

        Assert.False(response.Success);
        Assert.Null(response.Version);
        Assert.Null(response.BaselineOnRemoteParent);
    }

    [Fact]
    public async Task A_commit_that_is_not_in_the_repository_is_refused()
    {
        var response = await RunAsync("release/2.4", new string('a', 40));

        Assert.False(response.Success);
        Assert.Null(response.BaselineOnRemoteParent);
    }

    private Task<GetGitVersionAtBranchPointResponse> RunAsync(string parent, string sha) =>
        _command.ExecuteAsync(new GetGitVersionAtBranchPointRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "repo",
            ParentBranch = parent,
            CommitSha = sha,
        });

    private static string Git(string repo, string args) => GitConfigTestSupport.Git(repo, args).Trim();
}
