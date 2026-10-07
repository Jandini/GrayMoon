using GrayMoon.Worker.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Git CLI implementation of <see cref="IGitRepositoryReader"/>: local, read-only repository inspection through
/// <c>git.exe</c> on the read lane (see <c>RunGitReadAsync</c>). Holds the consolidated, process-reduced queries
/// (ref snapshot, commit-count probes, local-ref default-branch resolution) exactly as they were in
/// <see cref="GitService"/>. Never mutates repository state and never contacts a remote.
/// </summary>
public sealed class GitCliRepositoryReader(GitProcessRunner runner, ILogger<GitCliRepositoryReader> logger) : IGitRepositoryReader
{
    private static readonly Regex TrackAheadRegex = new(@"ahead (\d+)", RegexOptions.Compiled);
    private static readonly Regex TrackBehindRegex = new(@"behind (\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Every read goes through the read lane: no per-repository write lock, and <c>--no-optional-locks</c> in front
    /// of the subcommand so git never attempts an opportunistic index refresh that could collide with a concurrent
    /// writer's <c>index.lock</c>. This is a CLI implementation detail and is not part of <see cref="IGitRepositoryReader"/>.
    /// </summary>
    private Task<(int ExitCode, string? Stdout, string? Stderr)> RunGitReadAsync(
        string arguments,
        string repoPath,
        CancellationToken ct,
        bool? streamStderrAsStdout = null,
        bool? mirrorFailureOutputAsStderr = null)
        => runner.RunAsync(
            "git",
            "--no-optional-locks " + arguments,
            repoPath,
            ct,
            streamStderrAsStdout,
            mirrorFailureOutputAsStderr,
            GitLockIntent.Read);

    public async Task<string?> GetCurrentBranchNameAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        var (exitCode, stdout, _) = await RunGitReadAsync("branch --show-current", repoPath, ct);
        if (exitCode != 0)
            return null;

        var name = (stdout ?? "").Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public async Task<string?> GetHeadCommitAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        var (exitCode, stdout, _) = await runner.RunAsync("git", ["rev-parse", "HEAD"], repoPath, null, ct);
        if (exitCode != 0)
            return null;

        var sha = (stdout ?? "").Trim();
        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    public async Task<IReadOnlyList<string>> FindBranchCollisionsAsync(string repoPath, string branchName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return [];

        var name = branchName.Trim();
        var (exitCode, stdout, _) = await runner.RunAsync(
            "git",
            ["for-each-ref", "--format=%(refname:short)", $"refs/heads/{name}", $"refs/remotes/*/{name}"],
            repoPath,
            null,
            ct);
        if (exitCode != 0)
            return [];

        return (stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    public async Task<string?> RevParseAsync(string repoPath, string rev, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(rev))
            return null;

        var (exitCode, stdout, _) = await runner.RunAsync("git", ["rev-parse", rev.Trim()], repoPath, null, ct);
        if (exitCode != 0)
            return null;

        var sha = (stdout ?? "").Trim();
        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    public async Task<string?> GetRemoteOriginUrlAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        var (exitCode, stdout, stderr) = await runner.RunAsync("git", "config --get remote.origin.url", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogError("Git config remote.origin.url failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return null;
        }

        return (stdout ?? "").Trim();
    }

    public async Task<(int? Outgoing, int? Incoming, bool HasUpstream)> GetCommitCountsAsync(string repoPath, string branchName, string? defaultBranchOriginRef, CancellationToken ct, bool skipUpstreamCheck = false)
    {
        var probe = await ProbeCommitCountsAsync(repoPath, branchName, defaultBranchOriginRef, ct, skipUpstreamCheck);
        return (probe.Outgoing, probe.Incoming, probe.HasUpstream);
    }

    public async Task<CommitCountsProbeResult> ProbeCommitCountsAsync(string repoPath, string branchName, string? defaultBranchOriginRef, CancellationToken ct, bool skipUpstreamCheck = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return CommitCountsProbeResult.Unknown;

        var sw = Stopwatch.StartNew();

        // Whether the branch has a configured upstream is only knowable when we actually ask git for it.
        var upstreamProbed = !skipUpstreamCheck;

        // The divergence base is a file read. Its local branch is looked up first because every path without a
        // usable upstream needs it, and it rides along in the one listing below instead of costing its own
        // existence probe.
        var divergenceLocal = await GetDivergenceLocalBranchAsync(repoPath, ct);

        BranchTracking? tracking = skipUpstreamCheck
            ? null
            : await ReadBranchTrackingAsync(repoPath, branchName, divergenceLocal, ct);
        var upstreamRef = tracking?.Upstream;
        if (string.IsNullOrWhiteSpace(upstreamRef))
        {
            var compareRef = await ResolveNoUpstreamCompareRefAsync(repoPath, defaultBranchOriginRef, divergenceLocal, tracking?.DivergenceLocalExists, ct);
            return await CountAheadOfCompareRefAsync(repoPath, branchName, compareRef, upstreamProbed, sw, "no upstream", ct);
        }

        var originBranch = upstreamRef!;

        // The listing already said whether the upstream still exists ("gone" when its remote-tracking ref was
        // pruned or never fetched), so there is no separate existence probe.
        if (tracking!.UpstreamGone)
        {
            var compareRef = await ResolveNoUpstreamCompareRefAsync(repoPath, defaultBranchOriginRef, divergenceLocal, tracking.DivergenceLocalExists, ct);
            if (compareRef == null)
            {
                logger.LogDebug("Configured upstream for {Branch}, but remote {OriginBranch} not found and no compare ref for {RepoPath}, skipping commit counts", branchName, originBranch, repoPath);
                return new CommitCountsProbeResult(null, null, false, CountsProbed: false, UpstreamProbed: upstreamProbed);
            }

            return await CountAheadOfCompareRefAsync(repoPath, branchName, compareRef, upstreamProbed, sw, "missing remote upstream", ct);
        }

        // The checked-out branch: its ahead/behind against its upstream is what the rev-list below prints for
        // "<upstream>...HEAD", and the listing carries it already. Only when HEAD really is this branch - for any
        // other branch (a version provider can report a name that is not checked out, HEAD can be broken
        // mid-checkout) the counts have to be taken against HEAD, so those fall through to the rev-list.
        if (tracking.IsHead && tracking.CountsKnown)
        {
            sw.Stop();
            logger.LogDebug("GetCommitCounts completed in {ElapsedMs}ms for {RepoPath} (up{Outgoing} dn{Incoming}, from branch listing)", sw.ElapsedMilliseconds, repoPath, tracking.Ahead, tracking.Behind);
            return new CommitCountsProbeResult(tracking.Ahead, tracking.Behind, true, CountsProbed: true, UpstreamProbed: upstreamProbed);
        }

        // Single atomic call: left=incoming (in originBranch not HEAD), right=outgoing (in HEAD not originBranch).
        // originBranch is known to exist, but HEAD can still be unborn (no commits yet) right
        // after a checkout - an expected, already-handled miss here (falls back to unknown counts below),
        // not a real command failure, so it must not be mirrored to the overlay as a red stderr line.
        var (exitLR, stdoutLR, stderrLR) = await RunGitReadAsync(
            $"rev-list --left-right --count {originBranch}...HEAD",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitLR != 0)
        {
            logger.LogWarning("Git rev-list --left-right (commit counts) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitLR, stdoutLR, stderrLR);
            return new CommitCountsProbeResult(null, null, true, CountsProbed: false, UpstreamProbed: upstreamProbed);
        }

        var parts = (stdoutLR ?? "").Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
        var inVal = parts.Length >= 1 && int.TryParse(parts[0], out var ic) ? ic : (int?)null;
        var outVal = parts.Length >= 2 && int.TryParse(parts[1], out var oc) ? oc : (int?)null;
        sw.Stop();
        logger.LogDebug("GetCommitCounts completed in {ElapsedMs}ms for {RepoPath} (up{Outgoing} dn{Incoming})", sw.ElapsedMilliseconds, repoPath, outVal, inVal);
        return new CommitCountsProbeResult(outVal, inVal, true, CountsProbed: true, UpstreamProbed: upstreamProbed);
    }

    public async Task<(int? DefaultBehind, int? DefaultAhead, string? DefaultBranchName)> GetCommitCountsVsDefaultAsync(string repoPath, string? defaultBranchOriginRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (null, null, null);

        var defaultBranch = defaultBranchOriginRef ?? await GetDefaultBranchAsync(repoPath, ct);
        if (defaultBranch == null)
        {
            logger.LogDebug("GetCommitCountsVsDefault: no default branch for {RepoPath}", repoPath);
            return (null, null, null);
        }

        var sw = Stopwatch.StartNew();

        // Single call: --left-right gives both counts atomically; left=behind (in defaultBranch not HEAD), right=ahead (in HEAD not defaultBranch).
        // defaultBranch may be stale (resolved earlier, or not yet fetched) and simply not exist locally -
        // that is an expected, already-handled miss here, not a real command failure, so it must not be
        // mirrored to the overlay as a red stderr line (see RefExistsAsync for the same policy).
        var (exitCode, stdout, stderr) = await RunGitReadAsync(
            $"rev-list --left-right --count {defaultBranch}...HEAD",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitCode != 0)
        {
            logger.LogDebug("GetCommitCountsVsDefault failed for {RepoPath}. ExitCode={ExitCode}", repoPath, exitCode);
            return (null, null, null);
        }

        var parts = (stdout ?? "").Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
        var behind = parts.Length >= 1 && int.TryParse(parts[0], out var b) ? b : (int?)null;
        var ahead = parts.Length >= 2 && int.TryParse(parts[1], out var a) ? a : (int?)null;
        var defaultBranchName = defaultBranch.StartsWith("origin/") ? defaultBranch.Substring("origin/".Length) : defaultBranch;
        sw.Stop();
        logger.LogDebug("GetCommitCountsVsDefault completed in {ElapsedMs}ms for {RepoPath}: behind={Behind}, ahead={Ahead}", sw.ElapsedMilliseconds, repoPath, behind, ahead);
        return (behind, ahead, defaultBranchName);
    }

    public async Task<IReadOnlyList<string>> GetLocalBranchesAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return Array.Empty<string>();

        // strip=2 is the branch name itself; refname:short would turn a branch that shares its name with a tag into
        // "heads/<name>", which checks out as a detached HEAD.
        var (exitCode, stdout, stderr) = await RunGitReadAsync("for-each-ref refs/heads --format=%(refname:strip=2)", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogWarning("Git for-each-ref refs/heads failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return Array.Empty<string>();
        }

        return (stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .OrderBy(b => b)
            .ToList();
    }

    public async Task<RefSnapshot?> GetRefSnapshotAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        // One listing over the three namespaces. The fields (tab separated; a ref name cannot contain a tab):
        //   %(HEAD)            "*" on the local branch HEAD is attached to, otherwise " " (also " " when detached)
        //   %(refname)         full name; tells the namespaces apart and gives the origin branch names
        //   %(refname:strip=2) what "git tag" prints, and the local branch name "git branch --show-current" prints
        //   %(symref)          where a symbolic ref points; only refs/remotes/origin/HEAD is one that matters here,
        //                      and it is what the default branch is read from
        // Branch names are the full names below refs/heads/ and refs/remotes/origin/, never git's disambiguated
        // %(refname:short) ("heads/dup" when a tag is also called dup, "remotes/origin/x" when a local branch is
        // called origin/x), so they can be checked out and compared as they are.
        // --sort=-creatordate is "git tag --sort=-creatordate" applied to every ref; only the tags' relative order
        // is used, and the branch lists are sorted below exactly as the single-purpose reads sort them.
        var (exitCode, stdout, stderr) = await RunGitReadAsync(
            "for-each-ref --sort=-creatordate --format=%(HEAD)%09%(refname)%09%(refname:strip=2)%09%(symref) refs/heads refs/remotes/origin refs/tags",
            repoPath,
            ct);
        if (exitCode != 0)
        {
            // Callers fall back to the single-purpose reads, which keep their own failure handling.
            logger.LogDebug("Git for-each-ref (ref snapshot) failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}", repoPath, exitCode, stderr);
            return null;
        }

        const string remoteRefPrefix = "refs/remotes/origin/";
        var tags = new List<string>();
        var local = new List<string>();
        var remote = new List<string>();
        string? checkedOutBranch = null;

        // What the default-branch probes would have looked at, taken from the full ref names so the answer does
        // not depend on how git abbreviates an ambiguous name. A dangling origin/HEAD (its branch was pruned or
        // renamed on the remote) is not listed at all, which is the same answer the existence probe gave.
        var remoteBranchNames = new HashSet<string>(StringComparer.Ordinal);
        string? originHeadSymref = null;

        foreach (var rawLine in (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.TrimEnd('\r').Split('\t');
            if (parts.Length != 4)
                continue;

            var isHead = parts[0] == "*";
            var fullName = parts[1];
            var strippedName = parts[2].Trim();

            if (fullName.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(strippedName))
                    tags.Add(strippedName);
            }
            else if (fullName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(strippedName))
                    local.Add(strippedName);
                if (isHead && !string.IsNullOrWhiteSpace(strippedName))
                    checkedOutBranch = strippedName;
            }
            else if (fullName.StartsWith(remoteRefPrefix, StringComparison.Ordinal))
            {
                var remoteName = fullName[remoteRefPrefix.Length..];
                if (remoteName == "HEAD")
                {
                    originHeadSymref = parts[3];
                }
                else if (!string.IsNullOrWhiteSpace(remoteName))
                {
                    remoteBranchNames.Add(remoteName);
                    remote.Add(remoteName);
                }
            }
        }

        var originHeadTarget = OriginDefaultRef.HeadTargetBranch(originHeadSymref);
        var defaultOriginRef = OriginDefaultRef.Pick(originHeadTarget, remoteBranchNames.Contains);

        return new RefSnapshot(
            tags,
            local.OrderBy(b => b).ToList(),
            remote.OrderBy(b => b).ToList(),
            checkedOutBranch,
            defaultOriginRef,
            originHeadTarget != null && remoteBranchNames.Contains(originHeadTarget));
    }

    public async Task<IReadOnlyList<string>> GetRemoteBranchesFromRefsAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return Array.Empty<string>();

        // strip=3 is the name below refs/remotes/origin/; refname:short would turn it into "remotes/origin/<name>"
        // when a local branch is literally called origin/<name>, and the branch would drop out of the list.
        var (exitCode, stdout, stderr) = await RunGitReadAsync("for-each-ref refs/remotes/origin --format=%(refname:strip=3)", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogDebug("Git for-each-ref refs/remotes/origin failed for {RepoPath}. ExitCode={ExitCode}", repoPath, exitCode);
            return Array.Empty<string>();
        }

        return (stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => !string.IsNullOrWhiteSpace(b) && b != "HEAD")
            .OrderBy(b => b)
            .ToList();
    }

    public async Task<string?> GetDefaultBranchNameAsync(string repoPath, CancellationToken ct)
    {
        var defaultBranch = await GetDefaultBranchAsync(repoPath, ct);
        if (defaultBranch == null)
            return null;
        if (defaultBranch.StartsWith("origin/"))
            return defaultBranch.Substring("origin/".Length);
        return defaultBranch;
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return Array.Empty<string>();

        var (exitCode, stdout, stderr) = await RunGitReadAsync("tag --sort=-creatordate", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogDebug("Git tag list failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}", repoPath, exitCode, stderr);
            return Array.Empty<string>();
        }

        return (stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
    }

    public async Task<string?> GetCheckedOutTagAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        var (symExit, _, _) = await RunGitReadAsync("symbolic-ref -q HEAD", repoPath, ct);
        if (symExit == 0)
            return null;

        var (descExit, stdout, _) = await RunGitReadAsync("describe --tags --exact-match", repoPath, ct);
        if (descExit != 0)
            return null;

        var tag = (stdout ?? "").Trim();
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    public Task<string?> GetDefaultBranchOriginRefAsync(string repoPath, CancellationToken ct)
        => GetDefaultBranchAsync(repoPath, ct);

    public async Task<string?> GetDivergenceBaseBranchAsync(string repoPath, CancellationToken ct)
    {
        var path = await GitDirectoryLocator.ResolveDivergenceBaseFilePathAsync(runner, repoPath, ct);
        if (path is null || !File.Exists(path))
            return null;

        var text = (await File.ReadAllTextAsync(path, ct)).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Default branch as <c>origin/&lt;name&gt;</c>, or null. One listing answers the common cases: it shows
    /// where <c>origin/HEAD</c> points and whether <c>origin/main</c> / <c>origin/master</c> exist. Only when
    /// <c>origin/HEAD</c> points at some other branch is that branch's existence probed separately, and when the
    /// listing itself fails the original probe-by-probe lookup runs, so a failure here is never "no default".
    /// Sync does not come through here: it reads the same answer from its ref snapshot.
    /// </summary>
    private async Task<string?> GetDefaultBranchAsync(string repoPath, CancellationToken ct)
    {
        const string remoteRefPrefix = "refs/remotes/origin/";

        // A missing pattern is not an error for for-each-ref (exit 0, no line), and a dangling origin/HEAD is
        // silently left out, so the usual "not there" answers cost no failed process and paint nothing red.
        var (exit, stdout, _) = await RunGitReadAsync(
            "for-each-ref --format=%(refname)%09%(symref) refs/remotes/origin/HEAD refs/remotes/origin/main refs/remotes/origin/master",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exit != 0)
            return await GetDefaultBranchByProbesAsync(repoPath, ct);

        var existing = new HashSet<string>(StringComparer.Ordinal);
        string? headSymref = null;
        foreach (var rawLine in (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.TrimEnd('\r').Split('\t');
            if (parts.Length != 2 || !parts[0].StartsWith(remoteRefPrefix, StringComparison.Ordinal))
                continue;

            var name = parts[0][remoteRefPrefix.Length..];
            if (name == "HEAD")
                headSymref = parts[1];
            else
                existing.Add(name);
        }

        var headTarget = OriginDefaultRef.HeadTargetBranch(headSymref);
        if (headTarget != null && headTarget != "main" && headTarget != "master")
        {
            // The listing only looked at main and master, so it cannot say whether this branch exists.
            if (await RefExistsAsync(repoPath, $"origin/{headTarget}", ct))
                return $"origin/{headTarget}";
            headTarget = null;
        }

        return OriginDefaultRef.Pick(headTarget, existing.Contains);
    }

    /// <summary>The original lookup: one probe per step. Kept as the fallback for when the listing fails.</summary>
    private async Task<string?> GetDefaultBranchByProbesAsync(string repoPath, CancellationToken ct)
    {
        var (exitHead, stdoutHead, _) = await RunGitReadAsync(
            "symbolic-ref -q refs/remotes/origin/HEAD",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitHead == 0 && !string.IsNullOrWhiteSpace(stdoutHead))
        {
            var refName = stdoutHead.Trim();
            if (refName.StartsWith("refs/remotes/origin/"))
            {
                var branch = refName.Substring("refs/remotes/origin/".Length);
                if (!string.IsNullOrEmpty(branch) && branch != "HEAD")
                {
                    var originRef = $"origin/{branch}";
                    if (await RefExistsAsync(repoPath, originRef, ct))
                        return originRef;
                }
            }
        }

        if (await RefExistsAsync(repoPath, "origin/main", ct))
            return "origin/main";

        if (await RefExistsAsync(repoPath, "origin/master", ct))
            return "origin/master";

        return null;
    }

    public async Task<string?> GetUpstreamRefAsync(string repoPath, string branchName, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await RunGitReadAsync(
            $"for-each-ref --format=%(upstream:short) refs/heads/{branchName}",
            repoPath,
            ct);

        if (exitCode != 0)
            return null;

        var name = (stdout ?? "").Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Existence probe for a revision. Uses <c>rev-parse --verify --quiet</c> so a missing ref is a silent
    /// non-zero exit instead of <c>fatal: Needed a single revision</c> on the overlay (red). Overlay
    /// remirroring on failure is also off: these checks are expected to miss (try local, then remote;
    /// try origin/main, then origin/master).
    /// </summary>
    public async Task<bool> RefExistsAsync(string repoPath, string revision, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(revision))
            return false;

        var (exit, _, _) = await RunGitReadAsync(
            $"rev-parse --verify --quiet {revision}",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        return exit == 0;
    }

    /// <summary>
    /// When a branch has no usable upstream, Features count ahead of the local parent
    /// (<c>graymoon-divergence-base</c>) so a tip that still matches the parent reports 0.
    /// Workspace branches without a divergence base keep the default-branch fallback.
    /// </summary>
    /// <param name="divergenceLocal">The divergence base as a local branch name, from <see cref="GetDivergenceLocalBranchAsync"/>.</param>
    /// <param name="divergenceLocalExists">
    /// Whether that branch exists when the caller already learned it from a listing; null means "not known",
    /// and it is probed here.
    /// </param>
    private async Task<string?> ResolveNoUpstreamCompareRefAsync(
        string repoPath,
        string? defaultBranchOriginRef,
        string? divergenceLocal,
        bool? divergenceLocalExists,
        CancellationToken ct)
    {
        if (divergenceLocal != null)
        {
            var exists = divergenceLocalExists ?? await RefExistsAsync(repoPath, divergenceLocal, ct);
            if (exists)
                return divergenceLocal;
        }

        return defaultBranchOriginRef ?? await GetDefaultBranchAsync(repoPath, ct);
    }

    /// <summary>
    /// The divergence base (a Feature's parent branch) as a local branch name - without the <c>origin/</c> prefix
    /// - or null when there is none. A file read only; see <see cref="GetDivergenceBaseBranchAsync"/>.
    /// </summary>
    private async Task<string?> GetDivergenceLocalBranchAsync(string repoPath, CancellationToken ct)
    {
        var divergenceBase = await GetDivergenceBaseBranchAsync(repoPath, ct);
        if (string.IsNullOrWhiteSpace(divergenceBase))
            return null;

        var local = divergenceBase.Trim();
        if (local.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            local = local["origin/".Length..];
        return string.IsNullOrWhiteSpace(local) ? null : local;
    }

    /// <summary>
    /// What one <c>for-each-ref</c> says about a branch's upstream. <paramref name="Upstream"/> is null when the
    /// branch has none (or does not exist). <paramref name="Ahead"/>/<paramref name="Behind"/> are only to be used
    /// when <paramref name="CountsKnown"/> is set and the branch is the one HEAD is on.
    /// <paramref name="DivergenceLocalExists"/> is null when the divergence branch was not part of the listing.
    /// </summary>
    private sealed record BranchTracking(
        string? Upstream,
        bool UpstreamGone,
        bool IsHead,
        bool CountsKnown,
        int Ahead,
        int Behind,
        bool? DivergenceLocalExists);

    /// <summary>
    /// One <c>for-each-ref</c> for a branch: its configured upstream, whether that upstream is gone, whether HEAD
    /// is on the branch, and - for the checked-out branch - how far ahead and behind it is, which is what the
    /// separate upstream lookup, existence probe and <c>rev-list --left-right</c> used to answer in three
    /// processes. The divergence-base branch is listed too, so its existence needs no probe either. Null when the
    /// command fails, which callers treat as "no upstream", exactly as a failed upstream lookup always was.
    /// </summary>
    private async Task<BranchTracking?> ReadBranchTrackingAsync(
        string repoPath,
        string branchName,
        string? divergenceLocal,
        CancellationToken ct)
    {
        var branchRef = $"refs/heads/{branchName}";

        // The divergence branch only joins the listing when it is a plain name that cannot be mistaken for
        // several arguments or a pattern; otherwise it keeps its own probe. Patterns match a ref exactly or up
        // to a slash, so lines are matched on the full ref name below, never taken as they come.
        var divergenceRef = divergenceLocal != null && OriginDefaultRef.IsPlainRefName(divergenceLocal)
            ? $"refs/heads/{divergenceLocal}"
            : null;
        var patterns = divergenceRef == null || divergenceRef == branchRef ? branchRef : $"{branchRef} {divergenceRef}";

        var (exitCode, stdout, _) = await RunGitReadAsync(
            $"for-each-ref --format=%(HEAD)%09%(refname)%09%(upstream:short)%09%(upstream:track) {patterns}",
            repoPath,
            ct);
        if (exitCode != 0)
            return null;

        string? upstream = null;
        var gone = false;
        var isHead = false;
        var countsKnown = false;
        var ahead = 0;
        var behind = 0;
        var divergenceExists = false;

        foreach (var rawLine in (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.TrimEnd('\r').Split('\t');
            if (parts.Length != 4)
                continue;

            var fullName = parts[1];
            if (fullName == divergenceRef)
                divergenceExists = true;

            if (fullName != branchRef)
                continue;

            isHead = parts[0] == "*";
            var name = parts[2].Trim();
            upstream = string.IsNullOrWhiteSpace(name) ? null : name;

            // "[ahead 1, behind 2]", "[ahead 1]", "[behind 2]", "[gone]", or empty when level with the upstream.
            // The brackets are stripped here rather than asked of git (":nobracket" needs a newer git).
            var track = parts[3].Trim().Trim('[', ']');
            gone = track == "gone";
            var aheadMatch = TrackAheadRegex.Match(track);
            var behindMatch = TrackBehindRegex.Match(track);
            if (aheadMatch.Success)
                ahead = int.Parse(aheadMatch.Groups[1].Value);
            if (behindMatch.Success)
                behind = int.Parse(behindMatch.Groups[1].Value);

            // Empty means level with the upstream. Anything else that is not ahead/behind/gone is something this
            // code does not know, and a zero would be a wrong answer - leave the counts to the rev-list.
            countsKnown = track.Length == 0 || gone || aheadMatch.Success || behindMatch.Success;
        }

        return new BranchTracking(upstream, gone, isHead, countsKnown, ahead, behind, divergenceRef == null ? null : divergenceExists);
    }

    private async Task<CommitCountsProbeResult> CountAheadOfCompareRefAsync(
        string repoPath,
        string branchName,
        string? compareRef,
        bool upstreamProbed,
        Stopwatch sw,
        string reason,
        CancellationToken ct)
    {
        if (compareRef == null)
        {
            logger.LogDebug("No compare ref found for {RepoPath} ({Reason}), skipping commit counts for {Branch}", repoPath, reason, branchName);
            return new CommitCountsProbeResult(null, null, false, CountsProbed: false, UpstreamProbed: upstreamProbed);
        }

        // compareRef may be stale (resolved earlier, or not yet fetched) and simply not exist locally -
        // that is an expected, already-handled miss here, not a real command failure, so it must not be
        // mirrored to the overlay as a red stderr line (see RefExistsAsync for the same policy).
        var (exitDefault, stdoutDefault, stderrDefault) = await RunGitReadAsync(
            $"rev-list --count {compareRef}..HEAD",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitDefault != 0)
        {
            logger.LogWarning("Git rev-list (outgoing vs {CompareRef}) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", compareRef, repoPath, exitDefault, stdoutDefault, stderrDefault);
            return new CommitCountsProbeResult(null, null, false, CountsProbed: false, UpstreamProbed: upstreamProbed);
        }

        var aheadCount = int.TryParse((stdoutDefault ?? "").Trim(), out var ahead) ? ahead : (int?)null;
        sw.Stop();
        logger.LogDebug("GetCommitCounts (vs {CompareRef}, {Reason}) completed in {ElapsedMs}ms for {RepoPath}", compareRef, reason, sw.ElapsedMilliseconds, repoPath);
        return new CommitCountsProbeResult(aheadCount, null, false, CountsProbed: true, UpstreamProbed: upstreamProbed);
    }
}
