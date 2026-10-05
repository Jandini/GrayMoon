using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

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
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner);
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
    public async Task Probe_does_not_stream_fatal_when_upstream_exists_but_HEAD_is_unresolvable()
    {
        // Reproduces the exact three-dot form reported against the overlay: the configured upstream
        // (origin/main) resolves fine, but HEAD itself is unborn/broken at the moment of the probe
        // (e.g. mid-checkout race) - "fatal: ambiguous argument 'HEAD'" from the left-right rev-list,
        // not from a missing upstream.
        _repo.CommitInitial();
        var head = _repo.RunGit("rev-parse", "HEAD").Stdout.Trim();
        _repo.RunGit("update-ref", "refs/remotes/origin/main", head);
        _repo.RunGit("config", "branch.main.remote", "origin");
        _repo.RunGit("config", "branch.main.merge", "refs/heads/main");
        File.WriteAllText(Path.Combine(_repo.RepositoryPath, ".git", "HEAD"), "ref: refs/heads/does-not-exist\n");
        var events = new List<CommandLineStreamEvent>();
        using var _ = new CommandLineStreamScope(events.Add);

        var probe = await _git.ProbeCommitCountsAsync(_repo.RepositoryPath, "main", "origin/main", CancellationToken.None);

        Assert.False(probe.CountsProbed);
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

    [Fact]
    public async Task Probe_no_upstream_with_divergence_base_at_parent_tip_reports_zero_outgoing()
    {
        _repo.CommitInitial();
        _repo.RunGit("checkout", "-b", "feature");
        await _git.SetDivergenceBaseBranchAsync(_repo.RepositoryPath, "main", CancellationToken.None);

        var probe = await _git.ProbeCommitCountsAsync(_repo.RepositoryPath, "feature", "origin/main", CancellationToken.None);

        Assert.True(probe.CountsProbed);
        Assert.False(probe.HasUpstream);
        Assert.Equal(0, probe.Outgoing);
        Assert.Null(probe.Incoming);
    }

    [Fact]
    public async Task Probe_no_upstream_with_divergence_base_counts_only_feature_commits()
    {
        _repo.CommitInitial();
        _repo.RunGit("checkout", "-b", "feature");
        await _git.SetDivergenceBaseBranchAsync(_repo.RepositoryPath, "main", CancellationToken.None);
        _repo.WriteFile("feature.txt", "feature work\n");
        _repo.RunGit("add", "--all");
        _repo.RunGit("commit", "-m", "feature commit");

        var probe = await _git.ProbeCommitCountsAsync(_repo.RepositoryPath, "feature", "origin/main", CancellationToken.None);

        Assert.True(probe.CountsProbed);
        Assert.False(probe.HasUpstream);
        Assert.Equal(1, probe.Outgoing);
        Assert.Null(probe.Incoming);
    }

    [Fact]
    public async Task Probe_no_upstream_with_divergence_base_ignores_parent_ahead_of_origin_main()
    {
        _repo.CommitInitial();
        var initial = _repo.RunGit("rev-parse", "HEAD").Stdout.Trim();
        _repo.RunGit("update-ref", "refs/remotes/origin/main", initial);
        // Parent (main) moves ahead of origin/main before the Feature is cut.
        _repo.WriteFile("parent.txt", "parent work\n");
        _repo.RunGit("add", "--all");
        _repo.RunGit("commit", "-m", "parent ahead of origin/main");
        _repo.WriteFile("parent2.txt", "more parent\n");
        _repo.RunGit("add", "--all");
        _repo.RunGit("commit", "-m", "parent still ahead");
        _repo.RunGit("checkout", "-b", "feature");
        await _git.SetDivergenceBaseBranchAsync(_repo.RepositoryPath, "main", CancellationToken.None);

        var probe = await _git.ProbeCommitCountsAsync(_repo.RepositoryPath, "feature", "origin/main", CancellationToken.None);

        Assert.True(probe.CountsProbed);
        Assert.False(probe.HasUpstream);
        Assert.Equal(0, probe.Outgoing);
        // Without divergence base this would have been 2 (origin/main..HEAD).
        Assert.Null(probe.Incoming);
    }

    [Fact]
    public async Task Probe_no_upstream_without_divergence_base_still_counts_vs_default()
    {
        _repo.CommitInitial();
        var head = _repo.RunGit("rev-parse", "HEAD").Stdout.Trim();
        _repo.RunGit("update-ref", "refs/remotes/origin/main", head);
        _repo.RunGit("checkout", "-b", "workspace-branch");
        _repo.WriteFile("extra.txt", "ahead\n");
        _repo.RunGit("add", "--all");
        _repo.RunGit("commit", "-m", "ahead of origin/main");

        var probe = await _git.ProbeCommitCountsAsync(_repo.RepositoryPath, "workspace-branch", "origin/main", CancellationToken.None);

        Assert.True(probe.CountsProbed);
        Assert.False(probe.HasUpstream);
        Assert.Equal(1, probe.Outgoing);
        Assert.Null(probe.Incoming);
    }

    private static void AssertNoSingleRevisionFatal(List<CommandLineStreamEvent> events)
    {
        Assert.DoesNotContain(events, e => e.Text.Contains("Needed a single revision", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(events, e => e.Kind == WorkerCommandStreamKind.Stderr && e.Text.Contains("fatal:", StringComparison.OrdinalIgnoreCase));
    }
}
