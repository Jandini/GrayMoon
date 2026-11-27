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
/// FetchCommits is the remote-only observation the Feature removal queue runs for every Workspace repository
/// before a removal, in place of a Sync. These tests hold it to that: it reports what the fetch changed, it reports
/// what a Sync would report for the same repository, it starts no git process beyond the fetch, and it leaves the
/// checkout alone. Real git against a bare remote.
/// </summary>
public sealed class FetchCommitsCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-fetchcommits-").FullName;
    private readonly GitProcessCounter _counter;
    private readonly GitService _git;
    private readonly GitCliRepositoryReader _reader;
    private readonly string _repo;
    private readonly string _publisher;

    public FetchCommitsCommandTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        _counter = new GitProcessCounter(commandLine);
        var runner = new GitProcessRunner(_counter, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
        // origin.git, ws/repo on main with its upstream, and one commit on the remote the clone has not fetched.
        _repo = GitScenarios.CloneBehindRemote(_root);
        _publisher = Path.Combine(_root, "publisher");
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Reports_the_commits_merged_on_the_remote_as_incoming_and_starts_no_git_process_but_the_fetch()
    {
        var command = SnapshotCommand();
        await command.ExecuteAsync(Request()); // the first command of a process also runs the one-off safe.directory check
        PushFromPublisher("second.txt");
        _counter.Clear();

        var response = await command.ExecuteAsync(Request());

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(2, response.IncomingCommits);
        Assert.Equal(0, response.OutgoingCommits);
        Assert.True(response.HasUpstream);
        Assert.True(response.CommitCountsProbed);
        Assert.Equal(2, response.DefaultBranchBehind);
        Assert.Equal(0, response.DefaultBranchAhead);
        Assert.Equal("main", response.DefaultBranch);
        Assert.True(response.BranchesProbed);
        Assert.Equal(["main"], response.RemoteBranches);
        Assert.Equal(["main"], response.LocalBranches);
        // The local facts come from one in-process read: the fetch is the only git process.
        Assert.True(_counter.Count == 1, $"Expected only the fetch, saw:{Environment.NewLine}{_counter.Describe()}");
        Assert.Equal(1, _counter.CountOf("fetch origin --prune --tags"));
    }

    [Fact]
    public async Task Lists_a_branch_created_on_the_remote_and_drops_one_deleted_there()
    {
        GitConfigTestSupport.Git(_publisher, "push origin main:refs/heads/merged-later");
        var command = SnapshotCommand();
        var first = await command.ExecuteAsync(Request());
        Assert.Contains("merged-later", first.RemoteBranches!);

        GitConfigTestSupport.Git(_publisher, "push origin --delete merged-later");
        GitConfigTestSupport.Git(_publisher, "push origin main:refs/heads/next");
        GitConfigTestSupport.Git(_publisher, "tag v1.0.0");
        GitConfigTestSupport.Git(_publisher, "push origin v1.0.0");

        var second = await command.ExecuteAsync(Request());

        Assert.True(second.BranchesProbed);
        Assert.DoesNotContain("merged-later", second.RemoteBranches!);
        Assert.Contains("next", second.RemoteBranches!);
        Assert.Equal(["v1.0.0"], second.Tags);
    }

    [Fact]
    public async Task A_Sync_of_the_same_repository_reports_the_same_remote_observation()
    {
        // The explicit Sync is the oracle for the fetch-only step.
        GitConfigTestSupport.Git(_repo, "commit --allow-empty -m local-only");
        GitConfigTestSupport.Git(_publisher, "push origin main:refs/heads/other");
        GitConfigTestSupport.Git(_publisher, "tag v1.0.0");
        GitConfigTestSupport.Git(_publisher, "push origin v1.0.0");

        var fetched = await SnapshotCommand().ExecuteAsync(Request());
        var synced = await new SyncRepositoryCommand(
                _git, _reader, new LibGit2SharpLocalGitSnapshotReader(), new CountingCsProjFileService(), CapabilityTestDoubles.RealFactory(_git))
            .ExecuteAsync(new SyncRepositoryRequest
            {
                WorkspaceRoot = _root,
                WorkspaceName = "ws",
                RepositoryName = "repo",
                RepositoryId = 1,
                WorkspaceId = 1,
                Capabilities = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false),
            });

        Assert.True(fetched.Success, fetched.ErrorMessage);
        Assert.True(synced.Success, synced.ErrorMessage);
        Assert.Equal(1, fetched.OutgoingCommits);
        Assert.Equal(1, fetched.IncomingCommits);
        Assert.Equal(synced.OutgoingCommits, fetched.OutgoingCommits);
        Assert.Equal(synced.IncomingCommits, fetched.IncomingCommits);
        Assert.Equal(synced.HasUpstream, fetched.HasUpstream);
        Assert.Equal(synced.DefaultBranchBehind, fetched.DefaultBranchBehind);
        Assert.Equal(synced.DefaultBranchAhead, fetched.DefaultBranchAhead);
        Assert.Equal(synced.DefaultBranch, fetched.DefaultBranch);
        Assert.Equal(synced.RemoteBranches, fetched.RemoteBranches);
        Assert.Equal(synced.LocalBranches, fetched.LocalBranches);
        Assert.Equal(synced.Tags, fetched.Tags);
    }

    [Fact]
    public async Task The_git_CLI_reads_report_what_the_in_process_snapshot_reports()
    {
        GitConfigTestSupport.Git(_repo, "commit --allow-empty -m local-only");
        GitConfigTestSupport.Git(_publisher, "push origin main:refs/heads/other");
        GitConfigTestSupport.Git(_publisher, "tag v1.0.0");
        GitConfigTestSupport.Git(_publisher, "push origin v1.0.0");

        var fromSnapshot = await SnapshotCommand().ExecuteAsync(Request());
        var fromCli = await new FetchCommitsCommand(_git, _reader).ExecuteAsync(Request());

        Assert.True(fromCli.Success, fromCli.ErrorMessage);
        Assert.Equal(Describe(fromSnapshot), Describe(fromCli));
    }

    [Fact]
    public async Task Leaves_the_checked_out_commit_and_uncommitted_work_alone()
    {
        var headBefore = GitConfigTestSupport.Git(_repo, "rev-parse HEAD").Trim();
        var work = Path.Combine(_repo, "work.txt");
        await File.WriteAllTextAsync(work, "not committed\n");
        await File.WriteAllTextAsync(Path.Combine(_repo, "README.md"), "edited\n");

        var response = await SnapshotCommand().ExecuteAsync(Request());

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(1, response.IncomingCommits);
        Assert.Equal(headBefore, GitConfigTestSupport.Git(_repo, "rev-parse HEAD").Trim());
        Assert.Equal("main", GitConfigTestSupport.Git(_repo, "branch --show-current").Trim());
        Assert.Equal("not committed\n", await File.ReadAllTextAsync(work));
        Assert.Equal("edited\n", await File.ReadAllTextAsync(Path.Combine(_repo, "README.md")));
    }

    [Fact]
    public async Task A_tag_checkout_reports_the_tag_and_no_counts()
    {
        GitConfigTestSupport.Git(_repo, "tag v0.1.0");
        GitConfigTestSupport.Git(_repo, "checkout --quiet v0.1.0");

        var response = await SnapshotCommand().ExecuteAsync(Request());

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("v0.1.0", response.CurrentTag);
        Assert.False(response.CommitCountsProbed);
        Assert.Null(response.IncomingCommits);
        Assert.Null(response.OutgoingCommits);
        Assert.Null(response.HasUpstream);
        Assert.True(response.BranchesProbed);
    }

    [Fact]
    public async Task A_fetch_that_fails_reports_the_failure_and_nothing_else()
    {
        GitConfigTestSupport.Git(_repo, "remote set-url origin \"" + Path.Combine(_root, "missing.git") + "\"");

        var response = await SnapshotCommand().ExecuteAsync(Request());

        Assert.False(response.Success);
        Assert.False(string.IsNullOrWhiteSpace(response.ErrorMessage));
        Assert.False(response.BranchesProbed);
        Assert.False(response.CommitCountsProbed);
        Assert.Null(response.RemoteBranches);
    }

    private FetchCommitsCommand SnapshotCommand() => new(_git, _reader, new LibGit2SharpLocalGitSnapshotReader());

    private FetchCommitsRequest Request() => new()
    {
        WorkspaceRoot = _root,
        WorkspaceName = "ws",
        RepositoryName = "repo",
        RepositoryId = 1,
        WorkspaceId = 1,
    };

    private void PushFromPublisher(string file)
    {
        File.WriteAllText(Path.Combine(_publisher, file), "from the remote\n");
        GitConfigTestSupport.Git(_publisher, "add -A");
        GitConfigTestSupport.Git(_publisher, $"commit -m \"{file}\"");
        GitConfigTestSupport.Git(_publisher, "push origin main");
    }

    private static string Describe(FetchCommitsResponse r)
        => $"ok={r.Success} out={r.OutgoingCommits} in={r.IncomingCommits} upstream={r.HasUpstream} behind={r.DefaultBranchBehind} ahead={r.DefaultBranchAhead} "
            + $"counts={r.CommitCountsProbed} tag={r.CurrentTag} default={r.DefaultBranch} branches={r.BranchesProbed} "
            + $"local=[{string.Join(",", r.LocalBranches ?? [])}] remote=[{string.Join(",", r.RemoteBranches ?? [])}] tags=[{string.Join(",", r.Tags ?? [])}]";
}
