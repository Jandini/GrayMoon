using GrayMoon.Abstractions.Agent;
using GrayMoon.Agent.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Tests;

/// <summary>
/// Commit-count probes must not paint <c>fatal: Needed a single revision</c> red on the overlay when a
/// remote-tracking ref is simply not present yet (feature branch never pushed, origin/main not fetched).
/// The counts stay unknown; the repository is not mutated.
/// </summary>
public sealed class GitServiceCommitCountProbeTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly GitService _git;

    public GitServiceCommitCountProbeTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _git = new GitService(Options.Create(new AgentOptions()), NullLogger<GitService>.Instance, runner);
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Vs_default_does_not_stream_fatal_when_origin_main_is_missing()
    {
        _repo.CommitInitial();
        var events = new List<CommandLineStreamEvent>();
        using var _ = new CommandLineStreamScope(events.Add);

        var (behind, ahead, name) = await _git.GetCommitCountsVsDefaultAsync(_repo.RepositoryPath, "origin/main", CancellationToken.None);

        Assert.Null(behind);
        Assert.Null(ahead);
        Assert.Null(name);
        // The rev-list call still runs and still misses (origin/main does not exist) - only the overlay
        // presentation of that expected miss changed, not whether the command runs.
        Assert.Contains(events, e => e.Text.Contains("rev-list", StringComparison.Ordinal));
        AssertNoSingleRevisionFatal(events);
    }

    [Fact]
    public async Task Probe_does_not_stream_fatal_when_upstream_and_origin_main_are_missing()
    {
        _repo.CommitInitial();
        _repo.RunGit("checkout", "-b", "dropdown-highlight");
        _repo.RunGit("config", "branch.dropdown-highlight.remote", "origin");
        _repo.RunGit("config", "branch.dropdown-highlight.merge", "refs/heads/dropdown-highlight");
        var events = new List<CommandLineStreamEvent>();
        using var _ = new CommandLineStreamScope(events.Add);

        var probe = await _git.ProbeCommitCountsAsync(_repo.RepositoryPath, "dropdown-highlight", "origin/main", CancellationToken.None);

        Assert.False(probe.CountsProbed);
        Assert.Null(probe.Outgoing);
        Assert.Null(probe.Incoming);
        Assert.Contains(events, e => e.Text.Contains("rev-list", StringComparison.Ordinal));
        AssertNoSingleRevisionFatal(events);
    }

    [Fact]
    public async Task Vs_default_returns_ahead_count_when_origin_main_exists()
    {
        _repo.CommitInitial();
        var head = _repo.RunGit("rev-parse", "HEAD").Stdout.Trim();
        _repo.RunGit("update-ref", "refs/remotes/origin/main", head);
        _repo.RunGit("checkout", "-b", "feature");
        _repo.WriteFile("extra.txt", "ahead\n");
        _repo.RunGit("add", "--all");
        _repo.RunGit("commit", "-m", "ahead of origin/main");

        var (behind, ahead, name) = await _git.GetCommitCountsVsDefaultAsync(_repo.RepositoryPath, "origin/main", CancellationToken.None);

        Assert.Equal(0, behind);
        Assert.Equal(1, ahead);
        Assert.Equal("main", name);
    }

    private static void AssertNoSingleRevisionFatal(List<CommandLineStreamEvent> events)
    {
        Assert.DoesNotContain(events, e => e.Text.Contains("Needed a single revision", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(events, e => e.Kind == AgentCommandStreamKind.Stderr && e.Text.Contains("fatal:", StringComparison.OrdinalIgnoreCase));
    }
}
