using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The pull of a read-only repository: incoming commits are taken by fast-forward only, nothing is merged and nothing
/// is ever pushed. Real git against a local origin that is one commit ahead of the clone.
/// </summary>
public sealed class CommitSyncPullOnlyTests : IDisposable
{
    private static readonly RepositoryOperationCapabilities BasicWithoutVersioning
        = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false);

    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-pull-only-").FullName;
    private readonly GitService _git;
    private readonly GitCliRepositoryReader _reader;

    public CommitSyncPullOnlyTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
    }

    public void Dispose()
    {
        var root = OperatingSystem.IsWindows() ? @"\\?\" + _root : _root;
        try
        {
            // Git writes its object files read-only, which blocks the delete.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_fast_forward_pull_moves_HEAD_to_the_remote_commit_and_reports_it()
    {
        var repo = GitScenarios.CloneBehindRemote(_root);
        var remoteBefore = RemoteHead();

        var response = await PullAsync(pullOnly: true);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(remoteBefore, Head(repo));
        Assert.Equal(remoteBefore, response.Commit);
        Assert.Equal(0, response.IncomingCommits);
        Assert.Equal("from the remote\n", ReadReadme(repo));
        Assert.Equal(remoteBefore, RemoteHead());
    }

    [Fact]
    public async Task A_branch_with_a_local_commit_is_refused_and_neither_side_moves()
    {
        var repo = GitScenarios.CloneBehindRemote(_root);
        File.WriteAllText(Path.Combine(repo, "local.txt"), "mine\n");
        GitConfigTestSupport.Git(repo, "add -A");
        GitConfigTestSupport.Git(repo, "commit -q -m local");
        var headBefore = Head(repo);
        var remoteBefore = RemoteHead();

        var response = await PullAsync(pullOnly: true);

        Assert.False(response.Success);
        Assert.True(response.HasLocalCommits);
        Assert.False(response.LocalChangesBlockPull);
        Assert.Equal(GitRemoteIntegrateService.LocalCommitsMessage(1), response.ErrorMessage);
        Assert.Equal(headBefore, Head(repo));
        Assert.Equal(headBefore, response.Commit);
        // The local commit was not pushed, and the remote commit was not merged.
        Assert.Equal(remoteBefore, RemoteHead());
        Assert.Equal(1, response.OutgoingCommits);
    }

    [Fact]
    public async Task A_local_commit_alone_is_never_pushed()
    {
        var repo = GitScenarios.ClonedRepository(_root, push: true);
        File.WriteAllText(Path.Combine(repo, "local.txt"), "mine\n");
        GitConfigTestSupport.Git(repo, "add -A");
        GitConfigTestSupport.Git(repo, "commit -q -m local");
        var remoteBefore = RemoteHead();

        var response = await PullAsync(pullOnly: true);

        Assert.False(response.Success);
        Assert.True(response.HasLocalCommits);
        Assert.Equal(remoteBefore, RemoteHead());
    }

    [Fact]
    public async Task The_same_local_commit_is_pushed_when_the_repository_is_not_read_only()
    {
        var repo = GitScenarios.ClonedRepository(_root, push: true);
        File.WriteAllText(Path.Combine(repo, "local.txt"), "mine\n");
        GitConfigTestSupport.Git(repo, "add -A");
        GitConfigTestSupport.Git(repo, "commit -q -m local");

        var response = await PullAsync(pullOnly: false);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(Head(repo), RemoteHead());
    }

    [Fact]
    public async Task A_commit_on_a_detached_HEAD_is_not_pushed_and_the_error_says_the_repository_is_on_no_branch()
    {
        var repo = GitScenarios.ClonedRepository(_root, push: true);
        GitConfigTestSupport.Git(repo, "checkout -q --detach");
        File.WriteAllText(Path.Combine(repo, "local.txt"), "mine\n");
        GitConfigTestSupport.Git(repo, "add -A");
        GitConfigTestSupport.Git(repo, "commit -q -m detached");
        var remoteBefore = RemoteHead();

        var response = await PullAsync(pullOnly: false);

        Assert.False(response.Success);
        Assert.Equal(GitRemoteIntegrateService.NotOnBranchMessage, response.ErrorMessage);
        Assert.Contains("detached HEAD", response.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Create a branch", response.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(remoteBefore, RemoteHead());
    }

    [Fact]
    public async Task Local_changes_that_do_not_touch_the_incoming_files_do_not_block_the_pull()
    {
        var repo = GitScenarios.CloneBehindRemote(_root);
        File.WriteAllText(Path.Combine(repo, "notes.txt"), "untracked\n");

        var response = await PullAsync(pullOnly: true);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(RemoteHead(), Head(repo));
        Assert.Equal("untracked\n", File.ReadAllText(Path.Combine(repo, "notes.txt")));
    }

    [Fact]
    public async Task A_checkout_reports_the_commit_it_arrived_at()
    {
        var repo = GitScenarios.ClonedRepository(_root, push: true);
        GitConfigTestSupport.Git(repo, "branch other");

        var response = await CheckoutAsync("other");

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(Head(repo), response.Commit);
        Assert.False(response.LocalChangesBlockCheckout);
    }

    [Fact]
    public async Task A_checkout_blocked_by_local_changes_says_so_and_changes_nothing()
    {
        var repo = GitScenarios.ClonedRepository(_root, push: true);
        GitConfigTestSupport.Git(repo, "checkout -q -b other");
        File.WriteAllText(Path.Combine(repo, "README.md"), "on other\n");
        GitConfigTestSupport.Git(repo, "commit -q -a -m other");
        File.WriteAllText(Path.Combine(repo, "README.md"), "my edit\n");
        var headBefore = Head(repo);

        var response = await CheckoutAsync("main");

        Assert.False(response.Success);
        Assert.True(response.LocalChangesBlockCheckout);
        Assert.Null(response.Commit);
        Assert.Equal(headBefore, Head(repo));
        Assert.Equal("my edit\n", ReadReadme(repo));
    }

    private Task<CheckoutBranchResponse> CheckoutAsync(string branch) =>
        new CheckoutBranchCommand(_git, _reader).ExecuteAsync(new CheckoutBranchRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "repo",
            BranchName = branch,
        });

    [Fact]
    public async Task Local_changes_to_an_incoming_file_block_the_pull_and_stay_as_they_are()
    {
        var repo = GitScenarios.CloneBehindRemote(_root);
        File.WriteAllText(Path.Combine(repo, "README.md"), "my edit\n");
        var headBefore = Head(repo);

        var response = await PullAsync(pullOnly: true);

        Assert.False(response.Success);
        Assert.True(response.LocalChangesBlockPull);
        Assert.False(response.HasLocalCommits);
        Assert.False(response.MergeConflict);
        Assert.Equal(GitRemoteIntegrateService.LocalChangesBlockPullMessage, response.ErrorMessage);
        Assert.Equal(headBefore, Head(repo));
        Assert.Equal("my edit\n", ReadReadme(repo));
        Assert.Equal(1, response.IncomingCommits);
    }

    [Fact]
    public async Task A_pull_reports_the_projects_it_left_on_disk()
    {
        // The pull runs with hooks off, so no merge hook reports them: the App would keep the pre-pull references.
        GitScenarios.CloneBehindRemote(_root);
        var scanner = new CountingCsProjFileService();

        var response = await PullAsync(
            pullOnly: true,
            RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: true),
            scanner);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(1, scanner.FindCalls);
        Assert.NotNull(response.State);
        Assert.True(response.State.ProjectsProbed);
        Assert.NotNull(response.State.Projects);
    }

    [Fact]
    public async Task A_pull_in_a_workspace_that_does_not_discover_projects_scans_none()
    {
        GitScenarios.CloneBehindRemote(_root);
        var scanner = new CountingCsProjFileService();

        var response = await PullAsync(pullOnly: true, BasicWithoutVersioning, scanner);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(0, scanner.FindCalls);
        Assert.False(response.State!.ProjectsProbed);
    }

    [Fact]
    public async Task A_refused_pull_scans_no_projects()
    {
        var repo = GitScenarios.CloneBehindRemote(_root);
        File.WriteAllText(Path.Combine(repo, "README.md"), "my edit\n");
        var scanner = new CountingCsProjFileService();

        var response = await PullAsync(
            pullOnly: true,
            RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: true),
            scanner);

        Assert.False(response.Success);
        Assert.Equal(0, scanner.FindCalls);
        Assert.False(response.State!.ProjectsProbed);
    }

    private Task<CommitSyncRepositoryResponse> PullAsync(bool pullOnly) =>
        PullAsync(pullOnly, BasicWithoutVersioning, new CountingCsProjFileService());

    private Task<CommitSyncRepositoryResponse> PullAsync(
        bool pullOnly,
        RepositoryOperationCapabilities capabilities,
        CountingCsProjFileService projectScanner)
    {
        var versionProviders = CapabilityTestDoubles.RealFactory(_git);
        var command = new CommitSyncRepositoryCommand(
            _git,
            new GitRemoteIntegrateService(_git, _reader),
            new RepositoryStateProbe(_reader, projectScanner, versionProviders),
            versionProviders);
        return command.ExecuteAsync(new CommitSyncRepositoryRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "repo",
            WorkspaceId = 1,
            RepositoryId = 2,
            Capabilities = capabilities,
            PullOnly = pullOnly,
        });
    }

    private static string Head(string repo) => GitConfigTestSupport.Git(repo, "rev-parse HEAD").Trim();

    private string RemoteHead() => GitConfigTestSupport.Git(Path.Combine(_root, "origin.git"), "rev-parse refs/heads/main").Trim();

    private static string ReadReadme(string repo) => File.ReadAllText(Path.Combine(repo, "README.md")).Replace("\r\n", "\n");
}
