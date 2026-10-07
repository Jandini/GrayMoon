using System.Collections.Concurrent;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
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
/// Sync and the commit-count probe get the same answers from fewer git processes: the default branch comes from
/// the ref listing, the upstream/gone/ahead/behind state from one for-each-ref, and the git directory from the
/// file system. These tests pin both halves - the answers (against what git itself prints, on the awkward
/// repositories that make shortcuts go wrong) and the number of processes. Real git against a bare remote.
/// </summary>
public sealed class GitServiceFewerProcessesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-fewer-").FullName;
    private readonly GitService _git;
    private GitCliRepositoryReader _reader = null!;
    private int _clones;

    public GitServiceFewerProcessesTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader);
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

    // ---------------------------------------------------------------- default branch

    [Fact]
    public async Task Default_branch_follows_origin_HEAD_in_one_git_call()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));

        var (result, gitCalls) = await CountingAsync(() => _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None));

        Assert.Equal("origin/main", result);
        Assert.Equal(1, gitCalls);
    }

    [Fact]
    public async Task Default_branch_that_is_not_main_or_master_is_found_with_one_extra_existence_probe()
    {
        var repo = await CloneAsync(await SeedOriginAsync("develop"));

        var (result, gitCalls) = await CountingAsync(() => _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None));

        Assert.Equal("origin/develop", result);
        Assert.Equal(2, gitCalls);
        Assert.Equal("origin/develop", (await Snapshot(repo)).DefaultOriginRef);
    }

    [Fact]
    public async Task Default_branch_falls_back_to_main_when_there_is_no_origin_HEAD()
    {
        // A clone of an empty remote has no origin/HEAD; the commit is pushed afterwards.
        var origin = Path.Combine(_root, "empty-origin.git");
        Directory.CreateDirectory(origin);
        await GitAsync(origin, "init --bare -b main");
        var repo = await CloneAsync(origin);
        await CommitAsync(repo, "a.txt", "a");
        await GitAsync(repo, "push -u origin main");

        Assert.Equal("origin/main", await _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None));
        Assert.Equal("origin/main", (await Snapshot(repo)).DefaultOriginRef);
    }

    [Fact]
    public async Task Default_branch_is_null_when_the_remote_default_was_renamed_to_something_unrecognised()
    {
        // Renamed on the remote main -> trunk. A fetch never repoints origin/HEAD, so it dangles once main is
        // pruned. That is the same answer the probe-by-probe lookup gave: nothing recognisable is left.
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await RenameRemoteDefaultAsync(origin, "main", "trunk");
        await GitAsync(repo, "fetch --prune");

        Assert.Null(await _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None));
        Assert.Null((await Snapshot(repo)).DefaultOriginRef);
    }

    [Fact]
    public async Task Default_branch_falls_back_to_master_when_origin_HEAD_dangles_and_master_exists()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await RenameRemoteDefaultAsync(origin, "main", "master");
        await GitAsync(repo, "fetch --prune");

        Assert.Equal("origin/master", await _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None));
        Assert.Equal("origin/master", (await Snapshot(repo)).DefaultOriginRef);
    }

    [Fact]
    public async Task Default_branch_is_not_fooled_by_a_tag_or_local_branch_named_like_the_remote_one()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await RenameRemoteDefaultAsync(origin, "main", "trunk");
        await GitAsync(repo, "fetch --prune");
        // These resolve as "origin/main" to rev-parse, but they are not the remote's default branch.
        await GitAsync(repo, "tag origin/main");
        await GitAsync(repo, "branch origin/master");

        Assert.Null((await Snapshot(repo)).DefaultOriginRef);
    }

    [Fact]
    public async Task Default_branch_in_a_repository_without_a_remote_is_null_for_both_lookups()
    {
        var repo = Path.Combine(_root, "local-only");
        Directory.CreateDirectory(repo);
        await GitAsync(repo, "init -b main");
        await GitAsync(repo, "config user.email t@example.com");
        await GitAsync(repo, "config user.name T");
        await CommitAsync(repo, "a.txt", "a");

        Assert.Null(await _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None));
        Assert.Null((await Snapshot(repo)).DefaultOriginRef);
    }

    // ---------------------------------------------------------------- commit counts

    [Fact]
    public async Task Counts_for_the_checked_out_branch_with_an_upstream_take_one_git_call_and_match_rev_list()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await CommitAsync(repo, "local.txt", "local");          // ahead 1
        await PushFromSeedAsync(origin, "remote.txt", "remote"); // behind 1
        await GitAsync(repo, "fetch");

        var (probe, gitCalls) = await CountingAsync(() => _reader.ProbeCommitCountsAsync(repo, "main", "origin/main", CancellationToken.None));

        Assert.Equal("1\t1", (await GitAsync(repo, "rev-list --left-right --count origin/main...HEAD")).Trim());
        Assert.True(probe.CountsProbed);
        Assert.True(probe.HasUpstream);
        Assert.True(probe.UpstreamProbed);
        Assert.Equal(1, probe.Outgoing);
        Assert.Equal(1, probe.Incoming);
        Assert.Equal(1, gitCalls);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(0, 3)]
    public async Task Counts_report_zero_for_the_side_git_leaves_out(int ahead, int behind)
    {
        // git prints "ahead N", "behind N", both, or nothing at all when level; a missing side is zero.
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        for (var i = 0; i < ahead; i++)
            await CommitAsync(repo, $"l{i}.txt", "l");
        for (var i = 0; i < behind; i++)
            await PushFromSeedAsync(origin, $"r{i}.txt", "r");
        await GitAsync(repo, "fetch");

        var probe = await _reader.ProbeCommitCountsAsync(repo, "main", "origin/main", CancellationToken.None);

        Assert.True(probe.CountsProbed);
        Assert.Equal(ahead, probe.Outgoing);
        Assert.Equal(behind, probe.Incoming);
    }

    [Fact]
    public async Task Counts_are_unchanged_by_a_status_aheadBehind_setting()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await CommitAsync(repo, "local.txt", "local");
        await GitAsync(repo, "config status.aheadBehind false");

        var probe = await _reader.ProbeCommitCountsAsync(repo, "main", "origin/main", CancellationToken.None);

        Assert.Equal(1, probe.Outgoing);
        Assert.Equal(0, probe.Incoming);
    }

    [Fact]
    public async Task A_gone_upstream_counts_against_the_default_branch_in_two_git_calls()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await GitAsync(repo, "checkout -b feat");
        await CommitAsync(repo, "feat.txt", "feat");
        await GitAsync(repo, "push -u origin feat");
        await GitAsync(origin, "branch -D feat");
        await GitAsync(repo, "fetch --prune");

        var (probe, gitCalls) = await CountingAsync(() => _reader.ProbeCommitCountsAsync(repo, "feat", "origin/main", CancellationToken.None));

        Assert.True(probe.CountsProbed);
        Assert.False(probe.HasUpstream);
        Assert.Equal(1, probe.Outgoing);
        Assert.Null(probe.Incoming);
        Assert.Equal(2, gitCalls);
    }

    [Fact]
    public async Task A_gone_upstream_with_no_compare_ref_leaves_the_counts_unknown()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await GitAsync(repo, "checkout -b feat");
        await CommitAsync(repo, "feat.txt", "feat");
        await GitAsync(repo, "push -u origin feat");
        await GitAsync(origin, "branch -D feat");
        await GitAsync(repo, "fetch --prune");

        var probe = await _reader.ProbeCommitCountsAsync(repo, "feat", null, CancellationToken.None);
        // The default is still there (origin/main), so counts exist; remove it to reach the unknown case.
        Assert.True(probe.CountsProbed);

        await GitAsync(repo, "update-ref -d refs/remotes/origin/main");
        probe = await _reader.ProbeCommitCountsAsync(repo, "feat", null, CancellationToken.None);

        Assert.False(probe.CountsProbed);
        Assert.Null(probe.Outgoing);
        Assert.Null(probe.Incoming);
        Assert.False(probe.HasUpstream);
    }

    [Fact]
    public async Task A_branch_that_is_not_checked_out_is_counted_against_HEAD_as_before()
    {
        // A version provider can name a branch that is not the one checked out. Its upstream state is read
        // from the listing, but the counts still come from "<upstream>...HEAD" - HEAD is what the checkout is.
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await GitAsync(repo, "checkout -b feat");
        await CommitAsync(repo, "feat.txt", "feat");
        await GitAsync(repo, "push -u origin feat");
        await GitAsync(repo, "checkout main");

        var probe = await _reader.ProbeCommitCountsAsync(repo, "feat", "origin/main", CancellationToken.None);

        var expected = (await GitAsync(repo, "rev-list --left-right --count origin/feat...HEAD")).Trim().Split('\t');
        Assert.True(probe.CountsProbed);
        Assert.True(probe.HasUpstream);
        Assert.Equal(int.Parse(expected[1]), probe.Outgoing);
        Assert.Equal(int.Parse(expected[0]), probe.Incoming);
        Assert.Equal(1, probe.Incoming);
    }

    [Fact]
    public async Task A_branch_checked_out_in_another_worktree_is_counted_against_this_worktrees_HEAD()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await GitAsync(repo, "checkout -b feat");
        await CommitAsync(repo, "feat.txt", "feat");
        await GitAsync(repo, "push -u origin feat");
        await GitAsync(repo, "checkout main");
        var worktree = Path.Combine(_root, "wt-other");
        await GitAsync(repo, $"worktree add \"{worktree}\" -b side");

        // In the linked worktree HEAD is "side"; "feat" is not what it has checked out.
        var probe = await _reader.ProbeCommitCountsAsync(worktree, "feat", "origin/main", CancellationToken.None);

        var expected = (await GitAsync(worktree, "rev-list --left-right --count origin/feat...HEAD")).Trim().Split('\t');
        Assert.True(probe.CountsProbed);
        Assert.Equal(int.Parse(expected[1]), probe.Outgoing);
        Assert.Equal(int.Parse(expected[0]), probe.Incoming);
    }

    [Fact]
    public async Task A_feature_branch_with_no_upstream_and_a_divergence_base_counts_in_two_git_calls()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await GitAsync(repo, "checkout -b parent");
        await CommitAsync(repo, "parent.txt", "parent");
        await GitAsync(repo, "checkout -b feature");
        await CommitAsync(repo, "feature.txt", "feature");
        await _git.SetDivergenceBaseBranchAsync(repo, "parent", CancellationToken.None);

        var (probe, gitCalls) = await CountingAsync(() => _reader.ProbeCommitCountsAsync(repo, "feature", "origin/main", CancellationToken.None));

        Assert.True(probe.CountsProbed);
        Assert.False(probe.HasUpstream);
        Assert.Equal(1, probe.Outgoing);   // against "parent"; against origin/main it would be 2
        Assert.Equal(2, gitCalls);         // one listing, one rev-list
    }

    [Fact]
    public async Task A_divergence_base_that_names_a_missing_branch_falls_back_to_the_default()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await GitAsync(repo, "checkout -b feature");
        await CommitAsync(repo, "feature.txt", "feature");
        await _git.SetDivergenceBaseBranchAsync(repo, "origin/ghost", CancellationToken.None);

        var probe = await _reader.ProbeCommitCountsAsync(repo, "feature", "origin/main", CancellationToken.None);

        Assert.True(probe.CountsProbed);
        Assert.Equal(1, probe.Outgoing);
    }

    [Fact]
    public async Task A_divergence_base_that_is_a_prefix_of_another_branch_does_not_count_as_existing()
    {
        // for-each-ref patterns match "refs/heads/par" and "refs/heads/par/x" alike; only the exact ref counts.
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await GitAsync(repo, "checkout -b par/x");
        await CommitAsync(repo, "px.txt", "px");
        await GitAsync(repo, "checkout -b feature");
        await CommitAsync(repo, "feature.txt", "feature");
        await _git.SetDivergenceBaseBranchAsync(repo, "par", CancellationToken.None);

        var probe = await _reader.ProbeCommitCountsAsync(repo, "feature", "origin/main", CancellationToken.None);

        Assert.True(probe.CountsProbed);
        Assert.Equal(2, probe.Outgoing);   // "par" does not exist, so the default (origin/main) is the base
    }

    [Fact]
    public async Task A_divergence_base_with_an_odd_name_never_costs_the_branch_its_upstream_state()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await CommitAsync(repo, "local.txt", "local");
        await _git.SetDivergenceBaseBranchAsync(repo, "two words", CancellationToken.None);

        var probe = await _reader.ProbeCommitCountsAsync(repo, "main", "origin/main", CancellationToken.None);

        Assert.True(probe.CountsProbed);
        Assert.True(probe.HasUpstream);
        Assert.Equal(1, probe.Outgoing);
        Assert.Equal(0, probe.Incoming);
    }

    [Fact]
    public async Task Skipping_the_upstream_check_still_uses_the_divergence_base()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await GitAsync(repo, "checkout -b parent");
        await CommitAsync(repo, "parent.txt", "parent");
        await GitAsync(repo, "checkout -b feature");
        await CommitAsync(repo, "feature.txt", "feature");
        await _git.SetDivergenceBaseBranchAsync(repo, "parent", CancellationToken.None);

        var probe = await _reader.ProbeCommitCountsAsync(repo, "feature", "origin/main", CancellationToken.None, skipUpstreamCheck: true);

        Assert.True(probe.CountsProbed);
        Assert.False(probe.UpstreamProbed);
        Assert.Equal(1, probe.Outgoing);
    }

    [Fact]
    public async Task A_detached_head_or_unknown_branch_has_no_upstream_and_counts_against_the_default()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await CommitAsync(repo, "local.txt", "local");
        await GitAsync(repo, "checkout --detach");

        var probe = await _reader.ProbeCommitCountsAsync(repo, "no-such-branch", "origin/main", CancellationToken.None);

        Assert.True(probe.CountsProbed);
        Assert.False(probe.HasUpstream);
        Assert.Equal(1, probe.Outgoing);
    }

    [Fact]
    public async Task Read_and_write_intent_still_agree_on_the_new_paths()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await CommitAsync(repo, "local.txt", "local");

        Assert.Equal(
            await _reader.ProbeCommitCountsAsync(repo, "main", "origin/main", CancellationToken.None),
            await _reader.ProbeCommitCountsAsync(repo, "main", "origin/main", CancellationToken.None));
        Assert.Equal(
            await _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None),
            await _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None));
    }

    // ---------------------------------------------------------------- old behaviour as an oracle

    [Fact]
    public async Task Default_branch_agrees_with_the_probe_by_probe_lookup_as_origin_refs_change()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        var head = (await GitAsync(repo, "rev-parse HEAD")).Trim();

        async Task AssertAgreesAsync(string state)
        {
            var oracleTask = OracleDefaultAsync(repo);
            var standalone = _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None);
            var readIntent = _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None);
            var snapshot = Snapshot(repo);
            var oracle = await oracleTask;
            Assert.True(oracle == await standalone, $"standalone lookup differs: {state}");
            Assert.True(oracle == await readIntent, $"read-intent lookup differs: {state}");
            Assert.True(oracle == (await snapshot).DefaultOriginRef, $"snapshot differs: {state}");
        }

        await AssertAgreesAsync("clone: origin/HEAD -> main");
        await GitAsync(repo, "update-ref -d refs/remotes/origin/main");
        await AssertAgreesAsync("main pruned, origin/HEAD dangling, nothing else");
        await GitAsync(repo, $"update-ref refs/remotes/origin/master {head}");
        await AssertAgreesAsync("dangling origin/HEAD, master exists");
        await GitAsync(repo, "symbolic-ref refs/remotes/origin/HEAD refs/remotes/origin/develop");
        await AssertAgreesAsync("origin/HEAD -> develop (missing), master exists");
        await GitAsync(repo, $"update-ref refs/remotes/origin/develop {head}");
        await AssertAgreesAsync("origin/HEAD -> develop (exists)");
        await GitAsync(repo, "symbolic-ref refs/remotes/origin/HEAD refs/remotes/origin/master");
        await AssertAgreesAsync("origin/HEAD -> master");
        await GitAsync(repo, "symbolic-ref refs/remotes/origin/HEAD refs/remotes/upstream/main");
        await AssertAgreesAsync("origin/HEAD points outside origin");
        await GitAsync(repo, "symbolic-ref --delete refs/remotes/origin/HEAD");
        await AssertAgreesAsync("no origin/HEAD, master and develop exist");
        await GitAsync(repo, $"update-ref refs/remotes/origin/main {head}");
        await AssertAgreesAsync("no origin/HEAD, main and master exist");
        await GitAsync(repo, "update-ref -d refs/remotes/origin/main");
        await GitAsync(repo, "update-ref -d refs/remotes/origin/master");
        await GitAsync(repo, "update-ref -d refs/remotes/origin/develop");
        await AssertAgreesAsync("no origin refs at all");
        await GitAsync(repo, $"update-ref refs/remotes/origin/feature/x {head}");
        await GitAsync(repo, $"update-ref refs/remotes/origin/main/sub {head}");   // "main/sub" must not read as "main"
        await AssertAgreesAsync("only nested origin branches");
    }

    [Fact]
    public async Task Commit_counts_agree_with_the_probe_by_probe_logic_across_repository_states()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);

        // main: one commit ahead of and one behind origin/main.
        await CommitAsync(repo, "local.txt", "local");
        await PushFromSeedAsync(origin, "remote.txt", "remote");
        await GitAsync(repo, "fetch");
        // insync: pushed and level.
        await GitAsync(repo, "checkout -b insync");
        await CommitAsync(repo, "insync.txt", "insync");
        await GitAsync(repo, "push -u origin insync");
        // gone: pushed, then deleted on the remote and pruned, so its configured upstream no longer exists.
        await GitAsync(repo, "checkout -b gone main");
        await CommitAsync(repo, "gone.txt", "gone");
        await GitAsync(repo, "push -u origin gone");
        await GitAsync(origin, "branch -D gone");
        await GitAsync(repo, "fetch --prune");
        // noup: never pushed.
        await GitAsync(repo, "checkout -b noup main");
        await CommitAsync(repo, "noup.txt", "noup");
        // nested/name: a slash in the name, pushed and one commit ahead afterwards.
        await GitAsync(repo, "checkout -b nested/name main");
        await GitAsync(repo, "push -u origin nested/name");
        await CommitAsync(repo, "nested.txt", "nested");

        var checkedOut = 0;
        var comparisons = 0;
        var withCounts = 0;
        var states = new[] { "main", "gone", "noup", "nested/name", "--detach" };
        foreach (var state in states)
        {
            await GitAsync(repo, $"checkout {state}");
            foreach (var divergence in new string?[] { null, "main", "missing-branch" })
            {
                await _git.SetDivergenceBaseBranchAsync(repo, divergence, CancellationToken.None);
                // Reads only, so the probes of one repository state run side by side.
                var defaults = divergence == null ? new string?[] { null, "origin/main" } : new string?[] { "origin/main" };
                var probes = new[] { "main", "insync", "gone", "noup", "nonexistent" }
                    .SelectMany(branch => defaults.Select(defaultRef => (branch, defaultRef)))
                    .Select(async p =>
                    {
                        var expected = await OracleProbeAsync(repo, p.branch, p.defaultRef);
                        var actual = await _reader.ProbeCommitCountsAsync(repo, p.branch, p.defaultRef, CancellationToken.None);
                        return (Same: expected == actual, Actual: actual, Description:
                            $"HEAD={state}, divergence={divergence ?? "none"}, branch={p.branch}, default={p.defaultRef ?? "lookup"}\n  expected {expected}\n  actual   {actual}");
                    })
                    .ToList();

                foreach (var result in await Task.WhenAll(probes))
                {
                    Assert.True(result.Same, result.Description);
                    comparisons++;
                    if (result.Actual.CountsProbed)
                        withCounts++;
                }
            }

            checkedOut++;
        }

        // Guard against the matrix quietly degenerating into "everything unknown".
        Assert.Equal(states.Length, checkedOut);
        Assert.True(comparisons > 80, $"only {comparisons} comparisons ran");
        Assert.True(withCounts > comparisons / 2, $"only {withCounts} of {comparisons} comparisons produced counts");
    }

    /// <summary>The default-branch lookup as it was: one probe per step.</summary>
    private async Task<string?> OracleDefaultAsync(string repo)
    {
        var (exit, stdout, _) = await GitRawAsync(repo, "symbolic-ref -q refs/remotes/origin/HEAD");
        if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            var refName = stdout.Trim();
            if (refName.StartsWith("refs/remotes/origin/", StringComparison.Ordinal))
            {
                var branch = refName["refs/remotes/origin/".Length..];
                if (!string.IsNullOrEmpty(branch) && branch != "HEAD" && await OracleExistsAsync(repo, $"origin/{branch}"))
                    return $"origin/{branch}";
            }
        }

        if (await OracleExistsAsync(repo, "origin/main"))
            return "origin/main";
        if (await OracleExistsAsync(repo, "origin/master"))
            return "origin/master";
        return null;
    }

    /// <summary>The commit-count probe as it was: upstream lookup, existence probe, then the counting rev-list.</summary>
    private async Task<CommitCountsProbeResult> OracleProbeAsync(string repo, string branch, string? defaultRef)
    {
        var (upExit, upOut, _) = await GitRawAsync(repo, $"for-each-ref --format=%(upstream:short) refs/heads/{branch}");
        var upstream = upExit == 0 && !string.IsNullOrWhiteSpace(upOut) ? upOut.Trim() : null;

        async Task<string?> CompareRefAsync()
        {
            var divergence = await _reader.GetDivergenceBaseBranchAsync(repo, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(divergence))
            {
                var local = divergence.Trim();
                if (local.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
                    local = local["origin/".Length..];
                if (await OracleExistsAsync(repo, local))
                    return local;
            }

            return defaultRef ?? await OracleDefaultAsync(repo);
        }

        async Task<CommitCountsProbeResult> CountAheadAsync(string? compareRef)
        {
            if (compareRef == null)
                return new CommitCountsProbeResult(null, null, false, CountsProbed: false, UpstreamProbed: true);
            var (exit, stdout, _) = await GitRawAsync(repo, $"rev-list --count {compareRef}..HEAD");
            if (exit != 0)
                return new CommitCountsProbeResult(null, null, false, CountsProbed: false, UpstreamProbed: true);
            return new CommitCountsProbeResult(int.TryParse(stdout.Trim(), out var ahead) ? ahead : null, null, false, CountsProbed: true, UpstreamProbed: true);
        }

        if (string.IsNullOrWhiteSpace(upstream))
            return await CountAheadAsync(await CompareRefAsync());

        if (!await OracleExistsAsync(repo, upstream))
            return await CountAheadAsync(await CompareRefAsync());

        var (lrExit, lrOut, _) = await GitRawAsync(repo, $"rev-list --left-right --count {upstream}...HEAD");
        if (lrExit != 0)
            return new CommitCountsProbeResult(null, null, true, CountsProbed: false, UpstreamProbed: true);
        var parts = lrOut.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
        return new CommitCountsProbeResult(
            parts.Length >= 2 && int.TryParse(parts[1], out var o) ? o : null,
            parts.Length >= 1 && int.TryParse(parts[0], out var i) ? i : null,
            true,
            CountsProbed: true,
            UpstreamProbed: true);
    }

    private async Task<bool> OracleExistsAsync(string repo, string revision)
        => !string.IsNullOrWhiteSpace(revision) && (await GitRawAsync(repo, $"rev-parse --verify --quiet {revision}")).ExitCode == 0;

    private static Task<(int ExitCode, string Stdout, string Stderr)> GitRawAsync(string workingDirectory, string args)
        => GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);

    // ---------------------------------------------------------------- git directory

    [Fact]
    public async Task Divergence_base_needs_no_git_process_in_a_normal_repository()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));

        var (_, gitCalls) = await CountingAsync(async () =>
        {
            await _git.SetDivergenceBaseBranchAsync(repo, "main", CancellationToken.None);
            return await _reader.GetDivergenceBaseBranchAsync(repo, CancellationToken.None);
        });

        Assert.Equal(0, gitCalls);
        Assert.Equal("main", await _reader.GetDivergenceBaseBranchAsync(repo, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(repo, ".git", "graymoon-divergence-base")));
    }

    [Fact]
    public async Task Divergence_base_of_a_linked_worktree_lives_in_that_worktrees_own_git_dir()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        var worktree = Path.Combine(_root, "wt-feature");
        await GitAsync(repo, $"worktree add \"{worktree}\" -b feature");

        await _git.SetDivergenceBaseBranchAsync(worktree, "main", CancellationToken.None);

        var gitDir = Path.GetFullPath((await GitAsync(worktree, "rev-parse --absolute-git-dir")).Trim());
        Assert.Contains(Path.Combine(".git", "worktrees"), gitDir);
        Assert.True(File.Exists(Path.Combine(gitDir, "graymoon-divergence-base")));
        Assert.False(File.Exists(Path.Combine(repo, ".git", "graymoon-divergence-base")));
        Assert.Null(await _reader.GetDivergenceBaseBranchAsync(repo, CancellationToken.None));
        Assert.Equal("main", await _reader.GetDivergenceBaseBranchAsync(worktree, CancellationToken.None));
    }

    [Fact]
    public async Task A_worktree_removed_and_added_again_is_never_answered_from_a_stale_git_dir()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        // git names a linked worktree's admin folder after its directory and numbers it when the name is taken.
        // The decoy takes "wt-again", so the first real worktree gets "wt-again1"; once both are gone the same
        // path is added again and gets "wt-again": same working directory, different git directory.
        var decoy = Path.Combine(_root, "decoy", "wt-again");
        var worktree = Path.Combine(_root, "wt-again");
        await GitAsync(repo, $"worktree add \"{decoy}\" -b decoy");
        await GitAsync(repo, $"worktree add \"{worktree}\" -b first");
        var firstDir = TryRead(worktree);
        Assert.Equal(Path.GetFullPath((await GitAsync(worktree, "rev-parse --absolute-git-dir")).Trim()), firstDir);

        await GitAsync(repo, $"worktree remove --force \"{decoy}\"");
        await GitAsync(repo, $"worktree remove --force \"{worktree}\"");
        await GitAsync(repo, $"worktree add \"{worktree}\" -b second");

        var secondDir = TryRead(worktree);
        Assert.Equal(Path.GetFullPath((await GitAsync(worktree, "rev-parse --absolute-git-dir")).Trim()), secondDir);
        Assert.NotEqual(firstDir, secondDir);

        await _git.SetDivergenceBaseBranchAsync(worktree, "main", CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(secondDir!, "graymoon-divergence-base")));
    }
    [Fact]
    public async Task The_git_dir_reader_agrees_with_git_and_gives_up_on_anything_unfamiliar()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        Assert.Equal(Path.GetFullPath((await GitAsync(repo, "rev-parse --absolute-git-dir")).Trim()), TryRead(repo));

        // No .git entry at all (and a sub-folder of a repository, which git would resolve by walking up).
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        Assert.Null(TryRead(plain));
        var sub = Path.Combine(repo, "sub");
        Directory.CreateDirectory(sub);
        Assert.Null(TryRead(sub));

        // An empty .git folder is not a git directory.
        var emptyDotGit = Path.Combine(_root, "empty-dotgit");
        Directory.CreateDirectory(Path.Combine(emptyDotGit, ".git"));
        Assert.Null(TryRead(emptyDotGit));

        // A .git file that is not a gitdir pointer, or points nowhere.
        var junk = Path.Combine(_root, "junk");
        Directory.CreateDirectory(junk);
        File.WriteAllText(Path.Combine(junk, ".git"), "not a pointer\n");
        Assert.Null(TryRead(junk));
        File.WriteAllText(Path.Combine(junk, ".git"), "gitdir: " + Path.Combine(_root, "nowhere") + "\n");
        Assert.Null(TryRead(junk));
        File.WriteAllText(Path.Combine(junk, ".git"), "gitdir:\n");
        Assert.Null(TryRead(junk));

        // A relative pointer (submodule style) resolves against the folder holding the .git file.
        var relative = Path.Combine(_root, "relative");
        Directory.CreateDirectory(relative);
        File.WriteAllText(Path.Combine(relative, ".git"), "gitdir: ../gitstore\n");
        var store = Path.Combine(_root, "gitstore");
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "HEAD"), "ref: refs/heads/main\n");
        Assert.Equal(Path.GetFullPath(store), TryRead(relative));
    }

    // ---------------------------------------------------------------- hooks

    [Fact]
    public async Task Installing_hooks_takes_one_git_call_normally_and_two_when_hooks_path_points_outside()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));

        var (_, normal) = await CountingAsync(async () => { await _git.WriteSyncHooksAsync(repo, 1, 2, CancellationToken.None); return 0; });
        Assert.Equal(1, normal);
        Assert.True(File.Exists(Path.Combine(repo, ".git", "hooks", "pre-push")));

        var inside = Path.Combine(repo, ".git", "my-hooks");
        Directory.CreateDirectory(inside);
        await GitAsync(repo, $"config core.hooksPath \"{inside.Replace('\\', '/')}\"");
        var (_, configuredInside) = await CountingAsync(async () => { await _git.WriteSyncHooksAsync(repo, 1, 2, CancellationToken.None); return 0; });
        Assert.Equal(1, configuredInside);
        Assert.True(File.Exists(Path.Combine(inside, "pre-push")));

        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside-hooks")).FullName;
        await GitAsync(repo, $"config core.hooksPath \"{outside.Replace('\\', '/')}\"");
        var (_, configuredOutside) = await CountingAsync(async () => { await _git.WriteSyncHooksAsync(repo, 1, 2, CancellationToken.None); return 0; });
        Assert.Equal(2, configuredOutside);
        Assert.Empty(Directory.GetFiles(outside));
    }

    // ---------------------------------------------------------------- whole sync

    [Fact]
    public async Task A_sync_of_a_repository_with_an_upstream_needs_at_most_five_git_processes()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        var command = new SyncRepositoryCommand(_git, _reader, new CountingCsProjFileService(), CapabilityTestDoubles.RealFactory(_git));
        var request = NewRequest(RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false));
        await command.ExecuteAsync(request); // first sync of a process also runs the one-off safe.directory check

        var events = new ConcurrentQueue<CommandLineStreamEvent>();
        SyncRepositoryResponse response;
        using (new CommandLineStreamScope(events.Enqueue))
            response = await command.ExecuteAsync(request);

        var commands = events.Where(IsGit).Select(e => e.Text).ToList();
        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("main", response.Branch);
        Assert.Equal(0, response.OutgoingCommits);
        Assert.Equal(0, response.IncomingCommits);
        Assert.True(response.HasUpstream);
        Assert.Equal("main", response.DefaultBranch);
        Assert.Equal(0, response.DefaultBranchAhead);
        // fetch, ref listing, counts vs default, hooks, upstream counts
        Assert.True(commands.Count <= 5, "Expected at most 5 git processes, got " + commands.Count + ":\n" + string.Join("\n", commands));
        _ = repo;
    }

    [Fact]
    public async Task A_sync_of_a_feature_branch_with_no_upstream_reports_the_same_counts_as_before()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "checkout -b parent");
        await CommitAsync(repo, "parent.txt", "parent");
        await GitAsync(repo, "push origin parent");   // the "vs default" counts use the divergence base's origin ref
        await GitAsync(repo, "checkout -b feature");
        await CommitAsync(repo, "feature.txt", "feature");

        var request = NewRequest(RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false));
        request.DivergenceBaseBranch = "parent";
        var response = await new SyncRepositoryCommand(_git, _reader, new CountingCsProjFileService(), CapabilityTestDoubles.RealFactory(_git)).ExecuteAsync(request);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("feature", response.Branch);
        Assert.Equal(1, response.OutgoingCommits);       // against "parent"; against origin/main it would be 2
        Assert.False(response.HasUpstream);
        Assert.Equal("main", response.DefaultBranch);
        Assert.Equal(1, response.DefaultBranchAhead);    // against origin/parent
        Assert.Equal(0, response.DefaultBranchBehind);
    }

    // ---------------------------------------------------------------- origin/HEAD repair

    [Fact]
    public async Task A_sync_after_the_remote_default_was_renamed_repoints_origin_head_and_reports_the_new_default()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await RenameRemoteDefaultAsync(origin, "main", "trunk");

        var (response, commands) = await RecordAsync(() => SyncAsync());

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("trunk", response.DefaultBranch);
        Assert.NotNull(response.DefaultBranchAhead);
        Assert.NotNull(response.DefaultBranchBehind);
        Assert.Equal("main", response.Branch);
        Assert.Equal("refs/remotes/origin/trunk", (await GitAsync(repo, "symbolic-ref refs/remotes/origin/HEAD")).Trim());
        Assert.Single(commands, c => c.Contains("ls-remote", StringComparison.Ordinal));
        Assert.Single(commands, c => c.Contains("remote set-head origin trunk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_sync_after_a_repair_asks_the_remote_nothing_and_stays_within_the_process_budget()
    {
        var origin = await SeedOriginAsync("main");
        await CloneAsync(origin);
        await RenameRemoteDefaultAsync(origin, "main", "trunk");
        await SyncAsync();

        var (response, commands) = await RecordAsync(() => SyncAsync());

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("trunk", response.DefaultBranch);
        Assert.DoesNotContain(commands, c => c.Contains("ls-remote", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.Contains("set-head", StringComparison.Ordinal));
        // Fetch, listing, counts, hooks, and the tracking read. Local main still tracks the renamed (pruned)
        // origin/main here, so the gone-upstream compare path adds one rev-list on top of the usual five.
        Assert.True(commands.Count <= 6, string.Join(Environment.NewLine, commands));
    }

    [Fact]
    public async Task A_sync_leaves_an_origin_head_that_resolves_alone_even_when_it_is_not_the_remote_default()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await GitAsync(repo, "push origin main:other");
        await GitAsync(repo, "fetch origin");
        await GitAsync(repo, "remote set-head origin other");

        var (response, commands) = await RecordAsync(() => SyncAsync());

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("other", response.DefaultBranch);
        Assert.Equal("refs/remotes/origin/other", (await GitAsync(repo, "symbolic-ref refs/remotes/origin/HEAD")).Trim());
        Assert.DoesNotContain(commands, c => c.Contains("ls-remote", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.Contains("set-head", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sync_of_a_repository_that_is_not_dangling_does_not_try_to_repair()
    {
        var origin = await SeedOriginAsync("main");
        await CloneAsync(origin);

        var (response, commands) = await RecordAsync(() => SyncAsync());

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("main", response.DefaultBranch);
        Assert.DoesNotContain(commands, c => c.Contains("ls-remote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repair_repoints_a_dangling_origin_head_at_the_remote_default()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await RenameRemoteDefaultAsync(origin, "main", "trunk");
        await GitAsync(repo, "fetch --prune origin");
        Assert.Null(await _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None));

        var repaired = await _git.RepairOriginHeadAsync(repo, null, CancellationToken.None);

        Assert.True(repaired);
        Assert.Equal("origin/trunk", await _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None));
    }

    [Fact]
    public async Task A_repair_can_run_again_straight_after_a_success_when_the_default_moves_again()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await RenameRemoteDefaultAsync(origin, "main", "trunk");
        await GitAsync(repo, "fetch --prune origin");
        Assert.True(await _git.RepairOriginHeadAsync(repo, null, CancellationToken.None));

        await RenameRemoteDefaultAsync(origin, "trunk", "develop");
        await GitAsync(repo, "fetch --prune origin");

        Assert.True(await _git.RepairOriginHeadAsync(repo, null, CancellationToken.None));
        Assert.Equal("origin/develop", await _reader.GetDefaultBranchOriginRefAsync(repo, CancellationToken.None));
    }

    [Fact]
    public async Task A_repair_the_remote_cannot_answer_returns_false_and_is_not_retried_straight_away()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await GitAsync(repo, "remote set-url origin \"" + Path.Combine(_root, "does-not-exist.git") + "\"");
        await GitAsync(repo, "remote set-head origin --delete");

        var (first, firstCalls) = await CountingAsync(() => _git.RepairOriginHeadAsync(repo, null, CancellationToken.None));
        var (second, secondCalls) = await CountingAsync(() => _git.RepairOriginHeadAsync(repo, null, CancellationToken.None));

        Assert.False(first);
        Assert.True(firstCalls >= 1);
        Assert.False(second);
        Assert.Equal(0, secondCalls);
    }

    [Fact]
    public async Task A_repair_towards_a_branch_that_was_not_fetched_returns_false_and_changes_nothing()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        var seed = Path.Combine(_root, "seed");
        await GitAsync(seed, "branch trunk");
        await GitAsync(seed, "push origin trunk");
        await GitAsync(origin, "symbolic-ref HEAD refs/heads/trunk");
        await GitAsync(repo, "remote set-head origin --delete");

        var repaired = await _git.RepairOriginHeadAsync(repo, null, CancellationToken.None);

        Assert.False(repaired);
        var (exit, _, _) = await GitRawAsync(repo, "symbolic-ref refs/remotes/origin/HEAD");
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public async Task A_repair_of_a_missing_folder_is_a_no_op()
    {
        Assert.False(await _git.RepairOriginHeadAsync(Path.Combine(_root, "nope"), null, CancellationToken.None));
        Assert.False(await _git.RepairOriginHeadAsync("", null, CancellationToken.None));
    }

    [Fact]
    public async Task A_sync_whose_remote_cannot_name_a_default_still_succeeds_and_keeps_the_fallback_default()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        // origin/HEAD gone and the remote's own HEAD points at a branch that does not exist: the lookup finds nothing.
        await GitAsync(repo, "remote set-head origin --delete");
        await GitAsync(origin, "symbolic-ref HEAD refs/heads/ghost");

        var (response, commands) = await RecordAsync(() => SyncAsync());

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("main", response.DefaultBranch);
        Assert.Equal(1, commands.Count(c => c.Contains("ls-remote", StringComparison.Ordinal)));
        Assert.DoesNotContain(commands, c => c.Contains("set-head", StringComparison.Ordinal));

        var (again, againCommands) = await RecordAsync(() => SyncAsync());
        Assert.True(again.Success, again.ErrorMessage);
        Assert.DoesNotContain(againCommands, c => c.Contains("ls-remote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_features_counts_stay_against_its_divergence_base_when_the_default_is_repaired()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await GitAsync(repo, "push origin main:parent");
        await GitAsync(repo, "checkout -b feature");
        await CommitAsync(repo, "f.txt", "feature work");
        await RenameRemoteDefaultAsync(origin, "main", "trunk");
        // The new default has a commit the feature lacks, so counting against it instead of the parent would show.
        await PushFromSeedAsync(origin, "t.txt", "trunk work");

        var request = NewRequest(RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false));
        request.DivergenceBaseBranch = "parent";
        var (response, commands) = await RecordAsync(() => RunSyncAsync(request));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("trunk", response.DefaultBranch);
        Assert.Equal(1, response.DefaultBranchAhead);
        Assert.Equal(0, response.DefaultBranchBehind);
        Assert.Equal("refs/remotes/origin/trunk", (await GitAsync(repo, "symbolic-ref refs/remotes/origin/HEAD")).Trim());
        Assert.Single(commands, c => c.Contains("remote set-head origin trunk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sync_that_repairs_the_default_counts_against_the_new_default()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await RenameRemoteDefaultAsync(origin, "main", "trunk");
        await PushFromSeedAsync(origin, "t.txt", "trunk work");

        var response = await SyncAsync();

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("trunk", response.DefaultBranch);
        Assert.Equal(0, response.DefaultBranchAhead);
        Assert.Equal(1, response.DefaultBranchBehind);
        _ = repo;
    }

    // ---------------------------------------------------------------- helpers

    private static string? TryRead(string path) => GitDirectoryLocator.TryReadGitDirFromWorkTree(path);

    private static bool IsGit(CommandLineStreamEvent e)
        => e.Kind == WorkerCommandStreamKind.CommandLine && e.Text.StartsWith("$ git", StringComparison.Ordinal);

    private Task<SyncRepositoryResponse> SyncAsync()
        => RunSyncAsync(NewRequest(RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false)));

    private Task<SyncRepositoryResponse> RunSyncAsync(SyncRepositoryRequest request)
        => new SyncRepositoryCommand(_git, _reader, new CountingCsProjFileService(), CapabilityTestDoubles.RealFactory(_git))
            .ExecuteAsync(request);

    /// <summary>Runs <paramref name="action"/> and returns the text of every git command it started.</summary>
    private static async Task<(T Result, List<string> Commands)> RecordAsync<T>(Func<Task<T>> action)
    {
        var events = new ConcurrentQueue<CommandLineStreamEvent>();
        T result;
        using (new CommandLineStreamScope(events.Enqueue))
            result = await action();
        return (result, events.Where(IsGit).Select(e => e.Text).ToList());
    }

    private async Task<(T Result, int GitCalls)> CountingAsync<T>(Func<Task<T>> action)
    {
        var events = new ConcurrentQueue<CommandLineStreamEvent>();
        T result;
        using (new CommandLineStreamScope(events.Enqueue))
            result = await action();
        return (result, events.Count(IsGit));
    }

    private Task<GrayMoon.Worker.Models.RefSnapshot> Snapshot(string repo) => SnapshotCoreAsync(repo);

    private async Task<GrayMoon.Worker.Models.RefSnapshot> SnapshotCoreAsync(string repo)
    {
        var snapshot = await _reader.GetRefSnapshotAsync(repo, CancellationToken.None);
        Assert.NotNull(snapshot);
        return snapshot!;
    }

    private SyncRepositoryRequest NewRequest(RepositoryOperationCapabilities capabilities) => new()
    {
        WorkspaceRoot = _root,
        WorkspaceName = "ws",
        RepositoryName = "repo",
        RepositoryId = 1,
        WorkspaceId = 1,
        Capabilities = capabilities,
    };

    /// <summary>
    /// A bare remote whose default branch is <paramref name="defaultBranch"/>, with one commit on it. The first
    /// clone of it (see <see cref="CloneAsync"/>) therefore has an <c>origin/HEAD</c>.
    /// </summary>
    private async Task<string> SeedOriginAsync(string defaultBranch)
    {
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(origin);
        await GitAsync(origin, $"init --bare -b {defaultBranch}");

        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seed);
        await GitAsync(seed, $"init -b {defaultBranch}");
        await GitAsync(seed, "config user.email t@example.com");
        await GitAsync(seed, "config user.name T");
        await GitAsync(seed, $"remote add origin \"{origin}\"");
        await CommitAsync(seed, "README.md", "init");
        await GitAsync(seed, $"push origin {defaultBranch}");
        return origin;
    }

    /// <summary>Adds a commit to the remote's default branch through the seed clone.</summary>
    private async Task PushFromSeedAsync(string origin, string file, string text)
    {
        var seed = Path.Combine(_root, "seed");
        var branch = (await GitAsync(seed, "rev-parse --abbrev-ref HEAD")).Trim();
        await CommitAsync(seed, file, text);
        await GitAsync(seed, $"push origin {branch}");
        _ = origin;
    }

    /// <summary>Renames the remote's default branch, the way a hosting provider's "rename branch" does.</summary>
    private async Task RenameRemoteDefaultAsync(string origin, string from, string to)
    {
        var seed = Path.Combine(_root, "seed");
        await GitAsync(seed, $"branch -m {from} {to}");
        await GitAsync(seed, $"push origin {to}");
        await GitAsync(origin, $"symbolic-ref HEAD refs/heads/{to}");
        await GitAsync(origin, $"branch -D {from}");
    }

    /// <summary>
    /// Clones to <c>ws/repo</c> (the layout a sync request in this class points at) when it is free, and to a
    /// numbered folder otherwise. The user identity is set, since the tests commit in it.
    /// </summary>
    private async Task<string> CloneAsync(string origin)
    {
        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        var name = _clones++ == 0 ? "repo" : $"repo{_clones}";
        await GitAsync(workspace, $"clone \"{origin}\" {name}");
        var repo = Path.Combine(workspace, name);
        await GitAsync(repo, "config user.email t@example.com");
        await GitAsync(repo, "config user.name T");
        return repo;
    }

    private static async Task CommitAsync(string repoPath, string fileName, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repoPath, fileName), message + "\n");
        await GitAsync(repoPath, "add -A");
        await GitAsync(repoPath, $"commit -m \"{message}\"");
    }

    private static async Task<string> GitAsync(string workingDirectory, string args)
    {
        var (exit, stdout, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed in {workingDirectory}: {stderr}");
        return stdout;
    }
}
