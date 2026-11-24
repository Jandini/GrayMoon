using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Every command that reports a repository's identity reports its HEAD commit too, on a branch as well as on a tag.
/// The App decides the effective version per HEAD; a report without HEAD left the stored one behind, so an inherited
/// version outlived a commit made by Update and Push (log 2026-11-24 PUSH-UPDATE-DOES-NOT-COMPLETE).
/// Real git against a local origin.
/// </summary>
public sealed class HeadCommitReportTests : IDisposable
{
    private static readonly RepositoryOperationCapabilities NoVersioning
        = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false);

    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-head-report-").FullName;
    private readonly GitService _git;
    private readonly GitCliRepositoryReader _reader;

    public HeadCommitReportTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Refresh_version_after_a_commit_reports_the_new_HEAD()
    {
        var repoPath = CloneOnFeatureBranch();
        var head = Commit(repoPath);

        var response = await new RefreshRepositoryVersionCommand(_git, _reader, new NoWorkerToken(), CapabilityTestDoubles.RealFactory(_git))
            .ExecuteAsync(new RefreshRepositoryVersionRequest
            {
                WorkspaceRoot = _root,
                WorkspaceName = "ws",
                RepositoryName = "repo",
                Capabilities = NoVersioning,
            });

        Assert.Equal("feature/x", response.Branch);
        Assert.Equal(head, response.Commit);
    }

    [Fact]
    public async Task Refresh_version_of_a_folder_that_is_not_there_fails_instead_of_reporting_no_branch()
    {
        CloneOnFeatureBranch();

        // It used to answer branch "-", and the App stored "no branch" for a checkout nobody had read.
        var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            new RefreshRepositoryVersionCommand(_git, _reader, new NoWorkerToken(), CapabilityTestDoubles.RealFactory(_git))
                .ExecuteAsync(new RefreshRepositoryVersionRequest
                {
                    WorkspaceRoot = _root,
                    WorkspaceName = "ws",
                    RepositoryName = "gone",
                    Capabilities = NoVersioning,
                }));

        Assert.Contains("gone", error.Message);
    }

    [Fact]
    public async Task Create_branch_onto_a_branch_that_already_exists_reports_the_HEAD_it_checked_out()
    {
        var repoPath = CloneOnFeatureBranch();
        var featureHead = Commit(repoPath);
        GitConfigTestSupport.Git(repoPath, "checkout --detach HEAD~1");
        Assert.NotEqual(featureHead, Head(repoPath));

        // Prepare Workspace onto an existing branch runs with hooks off: this answer is the only report of the new HEAD.
        var response = await new CreateBranchCommand(_git, _reader, new NoWorkerToken())
            .ExecuteAsync(new CreateBranchRequest
            {
                WorkspaceRoot = _root,
                WorkspaceName = "ws",
                RepositoryName = "repo",
                NewBranchName = "feature/x",
                BaseBranchName = "main",
                SkipHooks = true,
                Capabilities = NoVersioning,
            });

        Assert.True(response.Success, response.ErrorMessage);
        Assert.True(response.AlreadyExisted);
        Assert.Null(response.BaseCommitSha);
        Assert.Equal(featureHead, response.HeadCommitSha);
    }

    [Fact]
    public async Task Sync_on_a_branch_reports_HEAD()
    {
        var repoPath = CloneOnFeatureBranch();
        var head = Commit(repoPath);

        var response = await new SyncRepositoryCommand(
                _git,
                _reader,
                new LibGit2SharpLocalGitSnapshotReader(),
                new CountingCsProjFileService(),
                CapabilityTestDoubles.RealFactory(_git))
            .ExecuteAsync(new SyncRepositoryRequest
            {
                WorkspaceRoot = _root,
                WorkspaceName = "ws",
                RepositoryName = "repo",
                RepositoryId = 1,
                WorkspaceId = 1,
                CloneUrl = Path.Combine(_root, "origin.git"),
                Capabilities = NoVersioning,
            });

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Null(response.Tag);
        Assert.Equal(head, response.Commit);
    }

    [Fact]
    public async Task Push_refresh_reports_HEAD()
    {
        var repoPath = CloneOnFeatureBranch();
        var head = Commit(repoPath);
        var command = new PushRepositoryCommand(
            _git, _reader, new CountingCsProjFileService(), CapabilityTestDoubles.RealFactory(_git),
            new GitRemoteIntegrateService(_git, _reader), new DisconnectedHubProvider(), NullLogger<PushRepositoryCommand>.Instance);

        var notification = await command.BuildPostOperationNotificationAsync(
            new PushRepositoryRequest { WorkspaceId = 1, RepositoryId = 2, Capabilities = NoVersioning },
            repoPath,
            "feature/x",
            versionOnly: false);

        Assert.True(notification.State!.IdentityProbed);
        Assert.Equal(head, notification.State.HeadCommit);
    }

    [Fact]
    public async Task Undo_push_refresh_reports_the_HEAD_the_reset_left()
    {
        var repoPath = CloneOnFeatureBranch();
        var before = Head(repoPath);
        Commit(repoPath);
        GitConfigTestSupport.Git(repoPath, "reset --soft HEAD~1");
        var command = new UndoPushCommand(_git, _reader, CapabilityTestDoubles.RealFactory(_git), new DisconnectedHubProvider(), NullLogger<UndoPushCommand>.Instance);

        var notification = await command.BuildPostResetNotificationAsync(
            new UndoPushRequest { WorkspaceId = 1, RepositoryId = 2, Capabilities = NoVersioning },
            repoPath,
            "feature/x");

        Assert.True(notification.State!.IdentityProbed);
        Assert.Equal(before, notification.State.HeadCommit);
    }

    /// <summary>A clone of an origin whose main has one commit, checked out on a local feature branch.</summary>
    private string CloneOnFeatureBranch()
    {
        GitTemplateCache.CopyTo("head-report-clone", _root, template =>
        {
            var seed = Path.Combine(template, "seed");
            Directory.CreateDirectory(seed);
            GitConfigTestSupport.Git(seed, "init -b main");
            GitConfigTestSupport.Git(seed, "config user.email test@example.com");
            GitConfigTestSupport.Git(seed, "config user.name Test");
            File.WriteAllText(Path.Combine(seed, "README.md"), "hello\n");
            GitConfigTestSupport.Git(seed, "add -A");
            GitConfigTestSupport.Git(seed, "commit -m init");

            var origin = Path.Combine(template, "origin.git");
            GitConfigTestSupport.Git(template, $"clone --bare \"{seed}\" \"{origin}\"");

            var workspace = Path.Combine(template, "ws");
            Directory.CreateDirectory(workspace);
            GitConfigTestSupport.Git(workspace, $"clone \"{origin}\" repo");

            var repoPath = Path.Combine(workspace, "repo");
            GitConfigTestSupport.Git(repoPath, "config user.email test@example.com");
            GitConfigTestSupport.Git(repoPath, "config user.name Test");
            GitConfigTestSupport.Git(repoPath, "checkout -b feature/x");
        });

        return Path.Combine(_root, "ws", "repo");
    }

    /// <summary>A commit of the branch's own, as the dependency update makes; returns the new HEAD.</summary>
    private static string Commit(string repoPath)
    {
        File.AppendAllText(Path.Combine(repoPath, "README.md"), "update\n");
        GitConfigTestSupport.Git(repoPath, "add -A");
        GitConfigTestSupport.Git(repoPath, "commit -m update");
        return Head(repoPath);
    }

    private static string Head(string repoPath) => GitConfigTestSupport.Git(repoPath, "rev-parse HEAD").Trim().ToLowerInvariant();
}
