using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The in-process local snapshot must answer exactly what the git CLI reads it replaces answer, on the repositories
/// that make shortcuts go wrong: detached and unborn HEADs, tag ties, gone and foreign upstreams, dangling
/// origin/HEAD, Feature divergence bases, shallow clones and linked worktrees. The CLI reader is the oracle. The
/// two deliberate differences (branch names are never git's disambiguated short form, and a comparison ref is
/// resolved by its full name so a tag never stands in for a branch) are pinned with explicit expectations.
/// Real git against a bare remote.
/// </summary>
public sealed class LocalGitSnapshotParityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-snapshot-").FullName;
    private readonly GitService _git;
    private readonly GitCliRepositoryReader _reader;
    private readonly LibGit2SharpLocalGitSnapshotReader _snapshots = new();
    private int _clones;

    public LocalGitSnapshotParityTests()
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

    // ---------------------------------------------------------------- branch / HEAD

    [Fact]
    public async Task An_attached_branch_with_an_upstream_matches_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal("main", snapshot.CurrentBranch);
        Assert.Equal("main", snapshot.Refs.CheckedOutBranch);
        Assert.False(snapshot.IsHeadUnborn);
        Assert.Equal(new CommitCountsProbeResult(0, 0, true, CountsProbed: true, UpstreamProbed: true), snapshot.CurrentBranchCounts);
    }

    [Fact]
    public async Task A_detached_head_at_a_plain_commit_matches_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await CommitAsync(repo, "a.txt", "a");
        await GitAsync(repo, "checkout -q --detach");

        var snapshot = await AssertParityAsync(repo);

        Assert.Null(snapshot.CurrentBranch);
        Assert.Null(snapshot.Refs.CheckedOutBranch);
        Assert.Null(snapshot.CheckedOutTag);
        Assert.Null(snapshot.CurrentBranchCounts);
        Assert.Equal(1, snapshot.DefaultAhead);
    }

    [Fact]
    public async Task An_unborn_branch_in_a_clone_of_an_empty_remote_matches_git()
    {
        var origin = Path.Combine(_root, "empty.git");
        Directory.CreateDirectory(origin);
        await GitAsync(origin, "init --bare -b main");
        var repo = await CloneAsync(origin);

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal("main", snapshot.CurrentBranch);
        Assert.Null(snapshot.Refs.CheckedOutBranch);
        Assert.True(snapshot.IsHeadUnborn);
        Assert.Null(snapshot.HeadSha);
        Assert.Empty(snapshot.Refs.Tags);
        Assert.Null(snapshot.Refs.DefaultOriginRef);
        Assert.False(snapshot.CurrentBranchCounts!.CountsProbed);
    }

    [Fact]
    public async Task A_repository_with_no_commits_and_no_remote_matches_git()
    {
        var repo = Path.Combine(_root, "bare-init");
        Directory.CreateDirectory(repo);
        await GitAsync(repo, "init -b trunk");

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal("trunk", snapshot.CurrentBranch);
        Assert.True(snapshot.IsHeadUnborn);
        Assert.False(snapshot.Refs.OriginHeadResolved);
    }

    [Fact]
    public async Task An_orphan_branch_in_a_repository_with_history_matches_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "checkout -q --orphan fresh");

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal("fresh", snapshot.CurrentBranch);
        Assert.True(snapshot.IsHeadUnborn);
        Assert.Null(snapshot.DefaultAhead);
    }

    // ---------------------------------------------------------------- tags

    [Fact]
    public async Task Tags_are_newest_first_with_ties_in_name_order_like_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        CommitAt(repo, "old.txt", 1_500_000_000);
        await GitAsync(repo, "tag old-b");
        await GitAsync(repo, "tag old-a");
        CommitAt(repo, "new.txt", 1_700_000_000);
        await GitAsync(repo, "tag zeta");
        await GitAsync(repo, "tag alpha");
        await GitAsync(repo, "tag rel/1.0");
        TagAt(repo, "ann-oldest", 1_400_000_000);
        TagAt(repo, "ann-newest", 1_800_000_000);
        TagAt(repo, "ann-tie-b", 1_600_000_000);
        TagAt(repo, "ann-tie-a", 1_600_000_000);

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal(
            ["ann-newest", "alpha", "rel/1.0", "zeta", "ann-tie-a", "ann-tie-b", "old-a", "old-b", "ann-oldest"],
            snapshot.Refs.Tags.Where(t => t != "init").ToList());
    }

    [Fact]
    public async Task The_checked_out_tag_follows_git_describe_among_lightweight_tags()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "tag light-b");
        await GitAsync(repo, "tag light-a");
        await GitAsync(repo, "checkout -q --detach light-b");

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal("light-a", snapshot.CheckedOutTag);
    }

    [Fact]
    public async Task The_checked_out_tag_prefers_the_newest_annotated_tag_like_git_describe()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "tag aaa-light");
        TagAt(repo, "ann-older", 1_500_000_000);
        TagAt(repo, "ann-newer", 1_600_000_000);
        TagAt(repo, "ann-also-older", 1_500_000_000);
        await GitAsync(repo, "checkout -q --detach aaa-light");

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal("ann-newer", snapshot.CheckedOutTag);
    }

    [Fact]
    public async Task A_tag_of_a_tag_and_a_tag_on_a_tree_match_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "tag -a inner -m inner");
        await GitAsync(repo, "tag -a outer -m outer inner");
        await GitAsync(repo, "tag tree-tag HEAD^{tree}");
        await GitAsync(repo, "checkout -q --detach");

        var snapshot = await AssertParityAsync(repo);

        Assert.Contains("outer", snapshot.Refs.Tags);
        Assert.Contains("tree-tag", snapshot.Refs.Tags);
    }

    // ---------------------------------------------------------------- branch lists

    [Fact]
    public async Task Branch_lists_match_git_on_an_awkward_repository()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "tag light1");
        await GitAsync(repo, "tag -a ann1 -m ann");
        await GitAsync(repo, "branch feature/x");
        await GitAsync(repo, "branch other");
        await CommitAsync(repo, "later.txt", "later");
        await GitAsync(repo, "push origin main:remote-only feature/x other --tags");
        await GitAsync(repo, "fetch --prune");

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal(["feature/x", "main", "other"], snapshot.Refs.LocalBranches);
        Assert.Equal(["feature/x", "main", "other", "remote-only"], snapshot.Refs.RemoteBranches);
    }

    [Fact]
    public async Task A_branch_that_shares_its_name_with_a_tag_is_listed_by_its_own_name()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "tag dup");
        await GitAsync(repo, "branch dup");
        await GitAsync(repo, "push origin refs/heads/dup:refs/heads/dup");
        await GitAsync(repo, "fetch --prune");

        var snapshot = await AssertParityAsync(repo);

        // git's refname:short would say "heads/dup", which checks out as a detached HEAD.
        Assert.Equal(["dup", "main"], snapshot.Refs.LocalBranches);
        Assert.Contains("dup", snapshot.Refs.RemoteBranches);
        Assert.Contains("dup", snapshot.Refs.Tags);
        Assert.Equal(snapshot.Refs.LocalBranches, await _reader.GetLocalBranchesAsync(repo, CancellationToken.None));
    }

    [Fact]
    public async Task A_local_branch_named_like_an_origin_branch_does_not_hide_the_origin_branch()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "push origin main:refs/heads/foo");
        await GitAsync(repo, "fetch --prune");
        await GitAsync(repo, "branch origin/foo");

        var snapshot = await AssertParityAsync(repo);

        // git's refname:short would say "remotes/origin/foo", and the branch dropped out of the remote list.
        Assert.Equal(["foo", "main"], snapshot.Refs.RemoteBranches);
        Assert.Equal(["main", "origin/foo"], snapshot.Refs.LocalBranches);
        Assert.Equal(snapshot.Refs.RemoteBranches, await _reader.GetRemoteBranchesFromRefsAsync(repo, CancellationToken.None));
    }

    // ---------------------------------------------------------------- default branch and origin/HEAD

    [Fact]
    public async Task A_dangling_origin_HEAD_after_the_remote_renamed_its_default_matches_git()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await RenameRemoteDefaultAsync(origin, "main", "trunk");
        await GitAsync(repo, "fetch --prune");

        var snapshot = await AssertParityAsync(repo);

        Assert.False(snapshot.Refs.OriginHeadResolved);
        Assert.Null(snapshot.Refs.DefaultOriginRef);
        Assert.Contains("trunk", snapshot.Refs.RemoteBranches);
    }

    [Fact]
    public async Task A_missing_origin_HEAD_falls_back_to_main_like_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "remote set-head origin -d");

        var snapshot = await AssertParityAsync(repo);

        Assert.False(snapshot.Refs.OriginHeadResolved);
        Assert.Equal("origin/main", snapshot.Refs.DefaultOriginRef);
    }

    [Fact]
    public async Task An_origin_HEAD_on_a_branch_other_than_main_or_master_matches_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("develop"));

        var snapshot = await AssertParityAsync(repo);

        Assert.True(snapshot.Refs.OriginHeadResolved);
        Assert.Equal("origin/develop", snapshot.Refs.DefaultOriginRef);
    }

    // ---------------------------------------------------------------- counts

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(0, 3)]
    [InlineData(2, 3)]
    public async Task Ahead_and_behind_the_upstream_match_git(int ahead, int behind)
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        for (var i = 0; i < behind; i++)
            await PushFromSeedAsync($"remote{i}.txt");
        for (var i = 0; i < ahead; i++)
            await CommitAsync(repo, $"local{i}.txt", $"local {i}");
        await GitAsync(repo, "fetch --prune");

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal(new CommitCountsProbeResult(ahead, behind, true, CountsProbed: true, UpstreamProbed: true), snapshot.CurrentBranchCounts);
        Assert.Equal(ahead, snapshot.DefaultAhead);
        Assert.Equal(behind, snapshot.DefaultBehind);
    }

    [Fact]
    public async Task A_gone_upstream_counts_against_the_default_like_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "checkout -q -b gone");
        await CommitAsync(repo, "g.txt", "g");
        await GitAsync(repo, "push -u origin gone");
        await GitAsync(repo, "push origin --delete gone");
        await GitAsync(repo, "fetch --prune");

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal(new CommitCountsProbeResult(1, null, false, CountsProbed: true, UpstreamProbed: true), snapshot.CurrentBranchCounts);
    }

    [Fact]
    public async Task A_branch_with_no_upstream_and_no_default_has_unknown_counts_like_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("trunk"));
        await GitAsync(repo, "remote set-head origin -d");
        await GitAsync(repo, "checkout -q -b lonely");

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal(new CommitCountsProbeResult(null, null, false, CountsProbed: false, UpstreamProbed: true), snapshot.CurrentBranchCounts);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("origin/parent")]
    public async Task A_feature_without_an_upstream_counts_against_its_local_parent_like_git(string divergenceBase)
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "checkout -q -b parent");
        await CommitAsync(repo, "p.txt", "p");
        await GitAsync(repo, "push origin parent");
        await GitAsync(repo, "checkout -q -b feature");
        await CommitAsync(repo, "f.txt", "f");

        var snapshot = await AssertParityAsync(repo, divergenceBase);

        Assert.Equal(new CommitCountsProbeResult(1, null, false, CountsProbed: true, UpstreamProbed: true), snapshot.CurrentBranchCounts);
        Assert.Equal(1, snapshot.DefaultAhead);   // against origin/parent
        Assert.Equal(0, snapshot.DefaultBehind);
    }

    [Fact]
    public async Task A_divergence_base_that_does_not_exist_falls_back_to_the_default_like_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "checkout -q -b feature");
        await CommitAsync(repo, "f.txt", "f");

        var snapshot = await AssertParityAsync(repo, "missing-parent");

        Assert.Equal(1, snapshot.CurrentBranchCounts!.Outgoing);
        Assert.Null(snapshot.DefaultAhead);   // origin/missing-parent does not exist
    }

    [Fact]
    public async Task An_upstream_on_another_remote_matches_git()
    {
        var origin = await SeedOriginAsync("main");
        var repo = await CloneAsync(origin);
        await GitAsync(repo, $"remote add upstream \"{origin}\"");
        await GitAsync(repo, "fetch upstream");
        await GitAsync(repo, "checkout -q -b up --track upstream/main");
        await CommitAsync(repo, "u.txt", "u");

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal(new CommitCountsProbeResult(1, 0, true, CountsProbed: true, UpstreamProbed: true), snapshot.CurrentBranchCounts);
    }

    [Fact]
    public async Task An_upstream_that_is_a_local_branch_matches_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "checkout -q -b topic");
        await CommitAsync(repo, "t.txt", "t");
        await GitAsync(repo, "branch --set-upstream-to=main");

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal(new CommitCountsProbeResult(1, 0, true, CountsProbed: true, UpstreamProbed: true), snapshot.CurrentBranchCounts);
    }

    [Fact]
    public async Task A_shallow_clone_matches_git()
    {
        var origin = await SeedOriginAsync("main");
        await PushFromSeedAsync("two.txt");
        await PushFromSeedAsync("three.txt");
        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        await GitAsync(workspace, $"clone --depth 1 \"{new Uri(origin).AbsoluteUri}\" shallow");
        var repo = Path.Combine(workspace, "shallow");
        await ConfigureUserAsync(repo);
        await CommitAsync(repo, "local.txt", "local");

        var snapshot = await AssertParityAsync(repo);

        Assert.Equal(1, snapshot.CurrentBranchCounts!.Outgoing);
        Assert.Equal(0, snapshot.CurrentBranchCounts.Incoming);
    }

    [Fact]
    public async Task A_tag_named_like_the_divergence_base_does_not_stand_in_for_the_branch()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "tag parent");                 // an older commit, under the branch's name
        await GitAsync(repo, "checkout -q -b parent");
        await CommitAsync(repo, "p.txt", "p");
        await GitAsync(repo, "checkout -q -b feature");
        await CommitAsync(repo, "f.txt", "f");

        var snapshot = _snapshots.Read(repo, new LocalGitSnapshotRequest("parent"), CancellationToken.None);

        // git's revision lookup prefers refs/tags/parent and would count 2; the branch is what is meant.
        Assert.Equal(1, snapshot.CurrentBranchCounts!.Outgoing);
    }

    // ---------------------------------------------------------------- linked worktrees

    [Fact]
    public async Task A_linked_worktree_matches_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        var worktree = Path.Combine(_root, "wt-feature");
        await GitAsync(repo, $"worktree add -q -b wt-feature \"{worktree}\"");
        await ConfigureUserAsync(worktree);
        await CommitAsync(worktree, "w1.txt", "w1");
        await GitAsync(worktree, "push -u origin wt-feature");
        await CommitAsync(worktree, "w2.txt", "w2");
        await GitAsync(worktree, "tag wt-tag");

        var snapshot = await AssertParityAsync(worktree, "main");

        Assert.Equal("wt-feature", snapshot.CurrentBranch);
        Assert.Equal(new CommitCountsProbeResult(1, 0, true, CountsProbed: true, UpstreamProbed: true), snapshot.CurrentBranchCounts);
        Assert.Equal(2, snapshot.DefaultAhead);
        Assert.Contains("wt-tag", snapshot.Refs.Tags);

        // The main checkout keeps its own HEAD; the refs are shared.
        var main = await AssertParityAsync(repo);
        Assert.Equal("main", main.CurrentBranch);
        Assert.Equal(snapshot.Refs.Tags, main.Refs.Tags);
    }

    [Fact]
    public async Task A_linked_worktree_without_an_upstream_counts_against_its_parent_like_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "branch parent");
        var worktree = Path.Combine(_root, "wt-child");
        await GitAsync(repo, $"worktree add -q -b child \"{worktree}\" parent");
        await ConfigureUserAsync(worktree);
        await CommitAsync(worktree, "c.txt", "c");

        var snapshot = await AssertParityAsync(worktree, "parent");

        Assert.Equal(new CommitCountsProbeResult(1, null, false, CountsProbed: true, UpstreamProbed: true), snapshot.CurrentBranchCounts);
    }

    [Fact]
    public async Task A_linked_worktree_detached_on_a_tag_matches_git()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await GitAsync(repo, "tag -a v1.0.0 -m v1");
        var worktree = Path.Combine(_root, "wt-tag");
        await GitAsync(repo, $"worktree add -q --detach \"{worktree}\" v1.0.0");

        var snapshot = await AssertParityAsync(worktree);

        Assert.Null(snapshot.CurrentBranch);
        Assert.Equal("v1.0.0", snapshot.CheckedOutTag);
    }

    // ---------------------------------------------------------------- failures and lifetime

    [Fact]
    public void A_folder_that_is_not_a_repository_cannot_be_read()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;

        var ex = Assert.Throws<LocalGitReadException>(() => _snapshots.Read(folder, new LocalGitSnapshotRequest(null), CancellationToken.None));

        Assert.Equal(folder, ex.RepositoryPath);
    }

    [Fact]
    public async Task A_repository_with_a_broken_HEAD_cannot_be_read()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "HEAD"), "garbage\n");

        Assert.Throws<LocalGitReadException>(() => _snapshots.Read(repo, new LocalGitSnapshotRequest(null), CancellationToken.None));
    }

    [Fact]
    public async Task A_bare_repository_cannot_be_read()
    {
        var origin = await SeedOriginAsync("main");

        Assert.Throws<LocalGitReadException>(() => _snapshots.Read(origin, new LocalGitSnapshotRequest(null), CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_read_stops()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _snapshots.Read(repo, new LocalGitSnapshotRequest(null), cts.Token));
    }

    [Fact]
    public async Task The_repository_is_released_when_the_read_returns()
    {
        var repo = await CloneAsync(await SeedOriginAsync("main"));
        var worktree = Path.Combine(_root, "wt-release");
        await GitAsync(repo, $"worktree add -q -b release \"{worktree}\"");

        _snapshots.Read(worktree, new LocalGitSnapshotRequest(null), CancellationToken.None);
        await GitAsync(repo, $"worktree remove \"{worktree}\"");

        Assert.False(Directory.Exists(worktree));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Takes the snapshot and checks every field against the git CLI read that answers the same question, after
    /// writing the divergence base file the way sync does (the CLI count probe reads it back).
    /// </summary>
    private async Task<LocalGitSnapshot> AssertParityAsync(string repo, string? divergenceBase = null)
    {
        var ct = CancellationToken.None;
        await _git.SetDivergenceBaseBranchAsync(repo, divergenceBase, ct);
        var cli = await _reader.GetRefSnapshotAsync(repo, ct);
        Assert.NotNull(cli);

        var snapshot = _snapshots.Read(repo, new LocalGitSnapshotRequest(divergenceBase), ct);

        Assert.Equal(cli!.Tags, snapshot.Refs.Tags);
        Assert.Equal(cli.LocalBranches, snapshot.Refs.LocalBranches);
        Assert.Equal(cli.RemoteBranches, snapshot.Refs.RemoteBranches);
        Assert.Equal(cli.CheckedOutBranch, snapshot.Refs.CheckedOutBranch);
        Assert.Equal(cli.DefaultOriginRef, snapshot.Refs.DefaultOriginRef);
        Assert.Equal(cli.OriginHeadResolved, snapshot.Refs.OriginHeadResolved);
        Assert.Equal(await _reader.GetCurrentBranchNameAsync(repo, ct), snapshot.CurrentBranch);
        Assert.Equal(await _reader.GetCheckedOutTagAsync(repo, ct), snapshot.CheckedOutTag);
        Assert.Equal(await _reader.GetHeadCommitAsync(repo, ct), snapshot.HeadSha);

        var divergenceRef = OriginDefaultRef.ToOriginBranchRef(divergenceBase) ?? cli.DefaultOriginRef;
        if (divergenceRef != null)
        {
            var (behind, ahead, _) = await _reader.GetCommitCountsVsDefaultAsync(repo, divergenceRef, ct);
            Assert.Equal(behind, snapshot.DefaultBehind);
            Assert.Equal(ahead, snapshot.DefaultAhead);
        }
        else
        {
            Assert.Null(snapshot.DefaultBehind);
            Assert.Null(snapshot.DefaultAhead);
        }

        if (snapshot.CurrentBranch != null)
            Assert.Equal(await _reader.ProbeCommitCountsAsync(repo, snapshot.CurrentBranch, cli.DefaultOriginRef, ct), snapshot.CurrentBranchCounts);
        else
            Assert.Null(snapshot.CurrentBranchCounts);

        return snapshot;
    }

    /// <summary>A bare remote whose default branch is <paramref name="defaultBranch"/>, with one commit (tagged nothing) on it.</summary>
    private async Task<string> SeedOriginAsync(string defaultBranch)
    {
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(origin);
        await GitAsync(origin, $"init --bare -b {defaultBranch}");

        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seed);
        await GitAsync(seed, $"init -b {defaultBranch}");
        await ConfigureUserAsync(seed);
        await GitAsync(seed, $"remote add origin \"{origin}\"");
        await CommitAsync(seed, "README.md", "init");
        await GitAsync(seed, $"push origin {defaultBranch}");
        return origin;
    }

    /// <summary>Adds a commit to the remote's default branch through the seed clone.</summary>
    private async Task PushFromSeedAsync(string file)
    {
        var seed = Path.Combine(_root, "seed");
        var branch = (await GitAsync(seed, "rev-parse --abbrev-ref HEAD")).Trim();
        await CommitAsync(seed, file, file);
        await GitAsync(seed, $"push origin {branch}");
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

    private async Task<string> CloneAsync(string origin)
    {
        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        var name = $"repo{_clones++}";
        await GitAsync(workspace, $"clone \"{origin}\" {name}");
        var repo = Path.Combine(workspace, name);
        await ConfigureUserAsync(repo);
        return repo;
    }

    /// <summary>A commit with a fixed committer date (git would give commits made in the same second the same date).</summary>
    private static void CommitAt(string repo, string file, long unixSeconds)
    {
        File.WriteAllText(Path.Combine(repo, file), file + "\n");
        using var repository = new Repository(repo);
        LibGit2Sharp.Commands.Stage(repository, file);
        var signature = new Signature("T", "t@example.com", DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        repository.Commit(file, signature, signature);
    }

    /// <summary>An annotated tag on HEAD with a fixed tagger date.</summary>
    private static void TagAt(string repo, string name, long unixSeconds)
    {
        using var repository = new Repository(repo);
        repository.ApplyTag(name, repository.Head.Tip.Sha, new Signature("T", "t@example.com", DateTimeOffset.FromUnixTimeSeconds(unixSeconds)), name);
    }

    private static async Task ConfigureUserAsync(string repo)
    {
        await GitAsync(repo, "config user.email t@example.com");
        await GitAsync(repo, "config user.name T");
    }

    private static async Task CommitAsync(string repoPath, string fileName, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repoPath, fileName), message + "\n");
        await GitAsync(repoPath, "add -A");
        await GitAsync(repoPath, $"commit -q -m \"{message}\"");
    }

    private static async Task<string> GitAsync(string workingDirectory, string args)
    {
        var (exit, stdout, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed in {workingDirectory}: {stderr}");
        return stdout;
    }
}
