using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Models;
using GrayMoon.Agent.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Services;

public sealed class GitService(IOptions<AgentOptions> options, ILogger<GitService> logger, GitProcessRunner runner) : IGitService
{
    private readonly int _listenPort = options.Value.ListenPort;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _safeRepoCache = new(StringComparer.OrdinalIgnoreCase);
    private static string? _emptyHooksPath;

    public string GetWorkspacePath(string root, string workspaceName)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Workspace root path is required.", nameof(root));
        var safe = SanitizeDirectoryName(workspaceName ?? "");
        return Path.Combine(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), safe);
    }

    public async Task<bool> CloneAsync(string workingDir, string cloneUrl, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDir))
            throw new ArgumentException("Working directory is required.", nameof(workingDir));
        if (string.IsNullOrWhiteSpace(cloneUrl))
            throw new ArgumentException("Clone URL is required.", nameof(cloneUrl));

        if (!Directory.Exists(workingDir))
            Directory.CreateDirectory(workingDir);

        var args = BuildCloneArguments(cloneUrl, bearerToken);
        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.ClonePipeline.ExecuteAsync(
            async (cancellationToken) => await runner.RunAsync("git", args, workingDir, cancellationToken),
            ct);
        sw.Stop();
        if (exitCode != 0)
        {
            logger.LogError("Git clone failed after retries in {ElapsedMs}ms. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, exitCode, stdout, stderr);
            return false;
        }
        logger.LogInformation("Git clone completed in {ElapsedMs}ms: {Url} -> {Dir}", sw.ElapsedMilliseconds, cloneUrl, workingDir);
        return true;
    }

    public async Task AddSafeDirectoryAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return;

        var fullPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (_safeRepoCache.ContainsKey(fullPath))
        {
            logger.LogDebug("Repository safety already verified in this process, skipping safe.directory check: {Path}", fullPath.Replace('\\', '/'));
            return;
        }

        var pathForGit = fullPath.Replace('\\', '/');

        var (isSafe, _) = await CheckRepoSafeAsync(repoPath, pathForGit, ct);
        logger.LogDebug("Git repo safety check: {Path} -> {Result}", pathForGit, isSafe ? "safe" : "not safe");

        if (isSafe)
        {
            _safeRepoCache.TryAdd(fullPath, 0);
            logger.LogDebug("Repository already safe, skipping safe.directory update: {Path}", pathForGit);
            return;
        }

        var addArgs = $"config --global --add safe.directory \"{pathForGit.Replace("\"", "\\\"")}\"";
        var (exitCode, stdout, stderr) = await runner.SafeDirectoryPipeline.ExecuteAsync(
            async (cancellationToken) => await runner.RunAsync("git", addArgs, repoPath, cancellationToken),
            ct);

        if (exitCode == 0)
        {
            _safeRepoCache.TryAdd(fullPath, 0);
            logger.LogDebug("Added safe.directory (global) for repository: {Path}", pathForGit);
        }
        else
            logger.LogError("Git config safe.directory (global) failed. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", exitCode, stdout, stderr);
    }

    private async Task<(bool IsSafe, bool IsDubiousOwnership)> CheckRepoSafeAsync(string repoPath, string pathForGit, CancellationToken ct)
    {
        var (exitCode, _, stderr) = await runner.RunAsync("git", "rev-parse --is-inside-work-tree", repoPath, ct);
        if (exitCode == 0)
            return (true, false);
        var err = stderr ?? "";
        var isDubious = exitCode == 128 && (err.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase) || err.Contains("safe.directory", StringComparison.OrdinalIgnoreCase));
        return (false, isDubious);
    }

    public async Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, CancellationToken ct)
        => await GetVersionAsync(repoPath, nonNormalize: false, commitSha: null, ct);

    public async Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, CancellationToken ct)
        => await GetVersionAsync(repoPath, nonNormalize, commitSha: null, ct);

    public async Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, string? commitSha, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (null, null);

        var (fileName, arguments) = GetGitVersionInvocation(repoPath, nonNormalize, commitSha);
        var toolName = fileName == "dotnet" ? "dotnet gitversion" : "dotnet-gitversion";

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.RunAsync(fileName, arguments, repoPath, ct);
        sw.Stop();
        if (exitCode != 0)
        {
            var manifestExists = File.Exists(Path.Combine(repoPath, "dotnet-tools.json"))
                || File.Exists(Path.Combine(repoPath, ".config", "dotnet-tools.json"));

            if (manifestExists && ShouldAttemptDotNetToolRestore(fileName, stderr, stdout))
            {
                logger.LogWarning("{ToolName} failed and tool manifest found. Running 'dotnet tool restore' in {RepoPath}", toolName, repoPath);
                var (restoreExitCode, _, restoreStderr) = await runner.RunAsync("dotnet", "tool restore", repoPath, ct);
                if (restoreExitCode != 0)
                {
                    logger.LogError("dotnet tool restore failed. ExitCode={ExitCode}, Stderr={Stderr}", restoreExitCode, restoreStderr);
                    runner.ReportOverlayStderr($"dotnet tool restore failed (exit {restoreExitCode}). {restoreStderr?.Trim()}");
                    return (null, $"dotnet tool restore failed: {restoreStderr?.Trim()}");
                }
                logger.LogInformation("dotnet tool restore succeeded in {RepoPath}. Retrying {ToolName}", repoPath, toolName);
                var (retryExitCode, retryStdout, retryStderr) = await runner.RunAsync(fileName, arguments, repoPath, ct);
                if (retryExitCode != 0)
                {
                    var retryError = BuildProcessError(retryStderr, retryStdout, $"{toolName} exited with code {retryExitCode}");
                    logger.LogError("{ToolName} failed after tool restore. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", toolName, retryExitCode, retryStdout, retryStderr);
                    return (null, retryError);
                }
                stdout = retryStdout;
            }
            else
            {
                var error = BuildProcessError(stderr, stdout, $"{toolName} exited with code {exitCode}");
                if (manifestExists)
                    logger.LogWarning("{ToolName} failed in {ElapsedMs}ms in {RepoPath}. Tool manifest exists, but failure does not look like a missing local tool; skipping 'dotnet tool restore'. ExitCode={ExitCode}", toolName, sw.ElapsedMilliseconds, repoPath, exitCode);
                logger.LogError("{ToolName} failed in {ElapsedMs}ms. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", toolName, sw.ElapsedMilliseconds, exitCode, stdout, stderr);
                return (null, error);
            }
        }
        logger.LogDebug("{ToolName} completed in {ElapsedMs}ms for {RepoPath}", toolName, sw.ElapsedMilliseconds, repoPath);

        try
        {
            return (JsonSerializer.Deserialize<GitVersionResult>(stdout ?? "", JsonOptions), null);
        }
        catch (JsonException ex)
        {
            runner.ReportOverlayStderr($"Failed to parse {toolName} JSON: {ex.Message}");
            return (null, $"Failed to parse {toolName} output: {ex.Message}");
        }
    }

    private static (string FileName, string Arguments) GetGitVersionInvocation(string repoPath, bool nonNormalize, string? commitSha)
    {
        const string manifestFileName = "dotnet-tools.json";
        var inRoot = Path.Combine(repoPath, manifestFileName);
        var inConfig = Path.Combine(repoPath, ".config", manifestFileName);
        var commonArgs = "/output json /nofetch /verbosity quiet";
        if (nonNormalize)
            commonArgs += " /nonormalize";
        if (!string.IsNullOrWhiteSpace(commitSha))
            commonArgs += " /c " + commitSha.Trim();
        if (File.Exists(inRoot) || File.Exists(inConfig))
            return ("dotnet", "gitversion " + commonArgs);
        return ("dotnet-gitversion", commonArgs);
    }

    public async Task<string?> GetCurrentBranchNameAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        var (exitCode, stdout, _) = await runner.RunAsync("git", "branch --show-current", repoPath, ct);
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

    public async Task<(bool Success, string? ErrorMessage)> FetchAsync(string repoPath, bool includeTags, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (true, null);

        string args;
        var logArgs = "";
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            args = includeTags ? "fetch origin --prune --tags" : "fetch origin --prune";
            logArgs = args;
        }
        else
        {
            var fetchCmd = includeTags ? "fetch origin --prune --tags" : "fetch origin --prune";
            args = $"{BuildAuthHeaderArgs(bearerToken)} {fetchCmd}";
            logArgs = "***";
        }
        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.FetchPipeline.ExecuteAsync(
            async cancellationToken => await runner.RunAsync("git", args, repoPath, cancellationToken),
            ct);
        sw.Stop();
        if (exitCode != 0)
        {
            var combined = CombineOutput(stdout, stderr) ?? $"Git fetch failed (exit code {exitCode})";
            logger.LogError("Git fetch failed in {ElapsedMs}ms for {RepoPath}. Args={Args}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, logArgs, exitCode, stdout, stderr);
            return (false, combined);
        }
        logger.LogDebug("Git fetch completed in {ElapsedMs}ms for {RepoPath}. Args={Args}", sw.ElapsedMilliseconds, repoPath, logArgs);
        return (true, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> FetchMinimalAsync(string repoPath, string branchName, string? defaultBranchOriginRef, string? bearerToken, CancellationToken ct, bool skipUpstreamCheck = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (true, null);

        logger.LogDebug("Git minimal fetch starting for {RepoPath}. InputBranch={Branch}, DefaultBranchOriginRef={DefaultRef}, HasBearer={HasBearer}",
            repoPath, branchName, defaultBranchOriginRef, !string.IsNullOrWhiteSpace(bearerToken));

        var refsToFetch = new List<string>();

        var upstreamRef = skipUpstreamCheck ? null : await GetUpstreamRefAsync(repoPath, branchName, ct);
        logger.LogDebug("Git minimal fetch upstream ref for {RepoPath}: {UpstreamRef}", repoPath, upstreamRef ?? "<none>");

        if (!string.IsNullOrWhiteSpace(upstreamRef))
        {
            var upstream = upstreamRef!;
            if (upstream.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
                upstream = upstream.Substring("origin/".Length);
            if (!string.IsNullOrWhiteSpace(upstream))
                refsToFetch.Add(upstream);
        }

        var defaultRef = defaultBranchOriginRef ?? await GetDefaultBranchAsync(repoPath, ct);
        logger.LogDebug("Git minimal fetch default branch ref for {RepoPath}: {DefaultRef}", repoPath, defaultRef ?? "<none>");

        if (!string.IsNullOrWhiteSpace(defaultRef))
        {
            var def = defaultRef!;
            if (def.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
                def = def.Substring("origin/".Length);
            if (!string.IsNullOrWhiteSpace(def) && !refsToFetch.Contains(def, StringComparer.OrdinalIgnoreCase))
                refsToFetch.Add(def);
        }

        if (refsToFetch.Count == 0)
        {
            logger.LogDebug("Git minimal fetch skipping for {RepoPath}: no refs to fetch.", repoPath);
            return (true, null);
        }

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await RunMinimalFetchAsync(repoPath, refsToFetch, bearerToken, ct);

        // A branch whose remote counterpart has been deleted keeps its upstream in git config, so it lands
        // in the ref list and takes the whole fetch down with it ("couldn't find remote ref demo"). The
        // branch simply not existing on the remote is state to report, not a failure to surface on the row,
        // so drop those refs and fetch what is left.
        if (exitCode != 0)
        {
            var missing = ParseMissingRemoteRefs(CombineOutput(stdout, stderr));
            if (missing.Count > 0)
            {
                var remaining = refsToFetch.Where(r => !missing.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
                logger.LogInformation("Git minimal fetch for {RepoPath}: remote ref(s) {Missing} no longer exist, retrying with {Remaining}",
                    repoPath, string.Join(", ", missing), remaining.Count == 0 ? "<none>" : string.Join(", ", remaining));

                if (remaining.Count == 0)
                {
                    sw.Stop();
                    return (true, null);
                }

                refsToFetch = remaining;
                (exitCode, stdout, stderr) = await RunMinimalFetchAsync(repoPath, refsToFetch, bearerToken, ct);
            }
        }

        sw.Stop();
        logger.LogDebug("Git minimal fetch git process completed for {RepoPath} in {ElapsedMs}ms. ExitCode={ExitCode}", repoPath, sw.ElapsedMilliseconds, exitCode);
        if (exitCode != 0)
        {
            var combined = CombineOutput(stdout, stderr) ?? $"Git fetch (minimal) failed (exit code {exitCode})";
            logger.LogError("Git minimal fetch failed in {ElapsedMs}ms for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, exitCode, stdout, stderr);
            return (false, combined);
        }

        logger.LogDebug("Git minimal fetch completed in {ElapsedMs}ms for {RepoPath}. Refs={Refs}", sw.ElapsedMilliseconds, repoPath, string.Join(", ", refsToFetch));
        return (true, null);
    }

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> RunMinimalFetchAsync(string repoPath, IReadOnlyList<string> refsToFetch, string? bearerToken, CancellationToken ct)
    {
        var refArgs = string.Join(" ", refsToFetch);
        var args = string.IsNullOrWhiteSpace(bearerToken)
            ? $"fetch origin --prune {refArgs}"
            : $"{BuildAuthHeaderArgs(bearerToken)} fetch origin --prune {refArgs}";

        logger.LogDebug("Git minimal fetch invoking git for {RepoPath}. Args={Args}, Refs={Refs}",
            repoPath, string.IsNullOrWhiteSpace(bearerToken) ? args : "***", string.Join(", ", refsToFetch));

        return await runner.MinimalFetchPipeline.ExecuteAsync(
            async cancellationToken => await runner.RunAsync("git", args, repoPath, cancellationToken),
            ct);
    }

    /// <summary>Ref names git reported as absent on the remote, from "couldn't find remote ref &lt;name&gt;" lines.</summary>
    internal static List<string> ParseMissingRemoteRefs(string? output)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
            return missing;

        foreach (Match match in MissingRemoteRefRegex.Matches(output))
        {
            var name = match.Groups[1].Value.Trim().TrimEnd('.', ',', ';', '\'', '"');
            if (name.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
                name = name.Substring("refs/heads/".Length);
            if (name.Length > 0 && !missing.Contains(name, StringComparer.OrdinalIgnoreCase))
                missing.Add(name);
        }

        return missing;
    }

    private static readonly Regex MissingRemoteRefRegex = new(
        @"couldn't find remote ref (\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        var upstreamRef = skipUpstreamCheck ? null : await GetUpstreamRefAsync(repoPath, branchName, ct);
        if (string.IsNullOrWhiteSpace(upstreamRef))
        {
            var compareRef = await ResolveNoUpstreamCompareRefAsync(repoPath, defaultBranchOriginRef, ct);
            return await CountAheadOfCompareRefAsync(repoPath, branchName, compareRef, upstreamProbed, sw, "no upstream", ct);
        }

        var originBranch = upstreamRef!;

        if (!await RefExistsAsync(repoPath, originBranch, ct))
        {
            var compareRef = await ResolveNoUpstreamCompareRefAsync(repoPath, defaultBranchOriginRef, ct);
            if (compareRef == null)
            {
                logger.LogDebug("Configured upstream for {Branch}, but remote {OriginBranch} not found and no compare ref for {RepoPath}, skipping commit counts", branchName, originBranch, repoPath);
                return new CommitCountsProbeResult(null, null, false, CountsProbed: false, UpstreamProbed: upstreamProbed);
            }

            return await CountAheadOfCompareRefAsync(repoPath, branchName, compareRef, upstreamProbed, sw, "missing remote upstream", ct);
        }

        // Single atomic call: left=incoming (in originBranch not HEAD), right=outgoing (in HEAD not originBranch).
        // originBranch was just confirmed to exist, but HEAD can still be unborn (no commits yet) right
        // after a checkout - an expected, already-handled miss here (falls back to unknown counts below),
        // not a real command failure, so it must not be mirrored to the overlay as a red stderr line.
        var (exitLR, stdoutLR, stderrLR) = await runner.RunAsync(
            "git",
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
        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
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

    public async Task<(bool Success, bool MergeConflict, string? ErrorMessage)> PullAsync(string repoPath, string branchName, string? bearerToken, CancellationToken ct, bool skipHooks = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, false, "Invalid repository path or branch name");

        var hooksPrefix = GetHooksConfigPrefix(skipHooks);

        string args;
        var logArgs = "";
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            args = $"{hooksPrefix}pull origin {branchName}";
            logArgs = args;
        }
        else
        {
            args = $"{BuildAuthHeaderArgs(bearerToken)} {hooksPrefix}pull origin {branchName}";
            logArgs = "***";
        }

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.PullPipeline.ExecuteAsync(
            async cancellationToken => await runner.RunAsync("git", args, repoPath, cancellationToken),
            ct);
        sw.Stop();

        if (exitCode != 0)
        {
            var combinedOutput = CombineOutput(stdout, stderr) ?? "";
            if (GitResiliencePipelines.IsMergeConflict(stdout, stderr))
            {
                logger.LogWarning("Git pull merge conflict detected for {RepoPath}. Branch={Branch}", repoPath, branchName);
                return (false, true, combinedOutput);
            }

            logger.LogError("Git pull failed in {ElapsedMs}ms for {RepoPath}. Args={Args}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, logArgs, exitCode, stdout, stderr);
            return (false, false, combinedOutput);
        }

        logger.LogInformation("Git pull completed in {ElapsedMs}ms for {RepoPath}. Args={Args}, Branch={Branch}", sw.ElapsedMilliseconds, repoPath, logArgs, branchName);
        return (true, false, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> PushAsync(string repoPath, string branchName, string? bearerToken, bool setTracking = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, "Invalid repository path or branch name");

        var pushOpts = setTracking ? "-u " : "";
        string args;
        var logArgs = "";
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            args = $"push {pushOpts}origin {branchName}";
            logArgs = args;
        }
        else
        {
            args = $"{BuildAuthHeaderArgs(bearerToken)} push {pushOpts}origin {branchName}";
            logArgs = "***";
        }

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.PushPipeline.ExecuteAsync(
            async cancellationToken => await runner.RunAsync("git", args, repoPath, cancellationToken),
            ct);
        sw.Stop();
        if (exitCode != 0)
        {
            var combined = CombineOutput(stdout, stderr) ?? "";
            logger.LogError("Git push failed in {ElapsedMs}ms for {RepoPath}. Args={Args}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, logArgs, exitCode, stdout, stderr);
            return (false, combined);
        }

        logger.LogInformation("Git push completed in {ElapsedMs}ms for {RepoPath}. Args={Args}, Branch={Branch}", sw.ElapsedMilliseconds, repoPath, logArgs, branchName);
        return (true, null);
    }

    public async Task AbortMergeAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return;

        var (exitCode, stdout, stderr) = await runner.RunAsync("git", "merge --abort", repoPath, ct);
        if (exitCode != 0)
            logger.LogWarning("Git merge --abort failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
        else
            logger.LogInformation("Git merge aborted for {RepoPath}", repoPath);
    }

    public async Task<(bool Success, bool HasConflicts, IReadOnlyList<string> ConflictFiles, string? ErrorMessage)> MergeFromRemoteAsync(string repoPath, string remoteBranch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (false, false, Array.Empty<string>(), "Invalid repository path");

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"merge --no-edit {remoteBranch}", repoPath, ct);
        sw.Stop();

        if (exitCode == 0)
        {
            logger.LogInformation("Git merge completed in {ElapsedMs}ms for {RepoPath}. Branch={Branch}", sw.ElapsedMilliseconds, repoPath, remoteBranch);
            return (true, false, Array.Empty<string>(), null);
        }

        // Exit code 1 with conflict markers = merge conflict; repo is left in MERGE_HEAD state.
        if (GitResiliencePipelines.IsMergeConflict(stdout, stderr))
        {
            logger.LogWarning("Git merge conflict detected in {ElapsedMs}ms for {RepoPath}. Branch={Branch}", sw.ElapsedMilliseconds, repoPath, remoteBranch);
            var conflictFiles = await GetConflictFilesAsync(repoPath, ct);
            return (false, true, conflictFiles, null);
        }

        var combined = CombineOutput(stdout, stderr) ?? "Merge failed";
        logger.LogError("Git merge failed in {ElapsedMs}ms for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, remoteBranch, exitCode, stdout, stderr);
        return (false, false, Array.Empty<string>(), combined);
    }

    private async Task<IReadOnlyList<string>> GetConflictFilesAsync(string repoPath, CancellationToken ct)
    {
        // Read-only: runs immediately after MergeFromRemoteAsync's own write-locked merge attempt has
        // already completed and released the lock, purely to enumerate the conflicted paths for the
        // caller. Matches the precedent already shipped in GitCliRepositoryGitChangesService, where a
        // write-locked mutation (stage/unstage/discard) is immediately followed by a Read-intent status
        // call to build the response snapshot - no caller of MergeFromRemoteAsync (only
        // UpdateBranchFromDefaultCommand) does anything else with the repo after this call returns, so
        // there is nothing for the write lock to order this against.
        var (exitCode, stdout, _) = await runner.RunAsync("git", "--no-optional-locks status --porcelain", repoPath, ct, intent: GitLockIntent.Read);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            return Array.Empty<string>();

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length >= 2 && (line[0] == 'U' || line[1] == 'U'
                || (line[0] == 'A' && line[1] == 'A')
                || (line[0] == 'D' && line[1] == 'D')))
            .Select(line => line.Substring(3).Trim())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetLocalBranchesAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return Array.Empty<string>();

        var (exitCode, stdout, stderr) = await runner.RunAsync("git", "for-each-ref refs/heads --format=%(refname:short)", repoPath, ct);
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

    public async Task<IReadOnlyList<string>> GetRemoteBranchesFromRefsAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return Array.Empty<string>();

        var (exitCode, stdout, stderr) = await runner.RunAsync("git", "for-each-ref refs/remotes/origin --format=%(refname:short)", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogDebug("Git for-each-ref refs/remotes/origin failed for {RepoPath}. ExitCode={ExitCode}", repoPath, exitCode);
            return Array.Empty<string>();
        }

        const string originPrefix = "origin/";
        return (stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => !string.IsNullOrWhiteSpace(b) && b.StartsWith(originPrefix, StringComparison.Ordinal))
            .Select(b => b.Substring(originPrefix.Length))
            .Where(b => !string.IsNullOrWhiteSpace(b) && b != "HEAD")
            .OrderBy(b => b)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return Array.Empty<string>();

        string args;
        var logArgs = "";
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            args = "ls-remote --heads origin";
            logArgs = args;
        }
        else
        {
            args = $"{BuildAuthHeaderArgs(bearerToken)} ls-remote --heads origin";
            logArgs = "***";
        }

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.LsRemotePipeline.ExecuteAsync(
            async cancellationToken => await runner.RunAsync("git", args, repoPath, cancellationToken),
            ct);
        sw.Stop();
        if (exitCode != 0)
        {
            logger.LogWarning("Git ls-remote failed in {ElapsedMs}ms for {RepoPath}. Args={Args}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, logArgs, exitCode, stdout, stderr);
            return Array.Empty<string>();
        }

        var branches = (stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line) && line.Contains("refs/heads/"))
            .Select(line =>
            {
                var parts = line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1].StartsWith("refs/heads/"))
                    return parts[1].Substring("refs/heads/".Length);
                return null;
            })
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .OrderBy(b => b!)
            .ToList();

        return branches!;
    }

    public async Task<(bool Success, string? ErrorMessage)> CheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct, bool skipHooks = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, "Invalid repository path or branch name");

        var hooksPrefix = GetHooksConfigPrefix(skipHooks);

        if (await RefExistsAsync(repoPath, $"refs/heads/{branchName}", ct))
        {
            var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"{hooksPrefix}checkout {branchName}", repoPath, ct);
            if (exitCode != 0)
            {
                logger.LogError("Git checkout failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, branchName, exitCode, stdout, stderr);
                return (false, CombineOutput(stdout, stderr));
            }
        }
        else
        {
            if (await RefExistsAsync(repoPath, $"origin/{branchName}", ct))
            {
                var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"{hooksPrefix}checkout -b {branchName} origin/{branchName}", repoPath, ct);
                if (exitCode != 0)
                {
                    logger.LogError("Git checkout failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, branchName, exitCode, stdout, stderr);
                    return (false, CombineOutput(stdout, stderr));
                }
            }
            else
            {
                var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"{hooksPrefix}checkout {branchName}", repoPath, ct);
                if (exitCode != 0)
                {
                    logger.LogError("Git checkout failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, branchName, exitCode, stdout, stderr);
                    return (false, CombineOutput(stdout, stderr));
                }
            }
        }

        logger.LogInformation("Git checkout completed for {RepoPath}. Branch={Branch}", repoPath, branchName);
        return (true, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> CreateBranchAsync(string repoPath, string newBranchName, string baseBranchName, CancellationToken ct, bool skipHooks = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(newBranchName) || string.IsNullOrWhiteSpace(baseBranchName))
            return (false, "Invalid repository path or branch name");

        var trimmedBase = baseBranchName.Trim();
        var startPoint = trimmedBase;
        var remoteCandidate = trimmedBase.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
            ? trimmedBase
            : "origin/" + trimmedBase;

        if (await RefExistsAsync(repoPath, remoteCandidate, ct))
            startPoint = remoteCandidate;

        var hooksPrefix = GetHooksConfigPrefix(skipHooks);

        var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"{hooksPrefix}checkout -b {newBranchName} --no-track {startPoint}", repoPath, ct);
        if (exitCode != 0)
        {
            if (await RefExistsAsync(repoPath, $"refs/heads/{newBranchName}", ct))
            {
                logger.LogWarning("Branch {Branch} already exists in {RepoPath}; checking out existing branch.", newBranchName, repoPath);
                var (coExit, coOut, coErr) = await runner.RunAsync("git", $"{hooksPrefix}checkout {newBranchName}", repoPath, ct);
                if (coExit != 0)
                {
                    logger.LogError("Git checkout of existing branch failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, newBranchName, coExit);
                    return (false, CombineOutput(coOut, coErr));
                }
                return (true, null);
            }
            logger.LogError("Git create branch failed for {RepoPath}. NewBranch={NewBranch}, BaseBranch={BaseBranch}, ExitCode={ExitCode}", repoPath, newBranchName, baseBranchName, exitCode);
            return (false, CombineOutput(stdout, stderr));
        }

        logger.LogInformation("Git branch created for {RepoPath}. NewBranch={NewBranch}, BaseBranch={BaseBranch}", repoPath, newBranchName, baseBranchName);
        return (true, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> DeleteBranchAsync(string repoPath, string branchName, bool isRemote, bool force, CancellationToken ct, bool skipHooks = false, string? bearerToken = null, string? expectedSha = null)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, "Invalid repository path or branch name");

        if (isRemote)
        {
            var name = branchName.Trim();
            if (name.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
                name = name.Substring("origin/".Length);
            if (string.IsNullOrWhiteSpace(name))
                return (false, "Invalid branch name");

            var defaultBranch = await GetDefaultBranchNameAsync(repoPath, ct);
            if (!string.IsNullOrWhiteSpace(defaultBranch)
                && name.Equals(defaultBranch.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Refusing to delete default branch {Branch} in {RepoPath}", name, repoPath);
                return (false, "Cannot delete the repository default branch.");
            }

            var hooksPrefix = GetHooksConfigPrefix(skipHooks);

            // Lease-based delete (D4): fetch so the lease compares against the current remote tip, then
            // push a delete that refuses when origin/<name> is no longer at expectedSha.
            if (!string.IsNullOrWhiteSpace(expectedSha))
            {
                // Always fetch this branch's remote tip (plus default when known) so the lease compares
                // against current origin state, even when the local branch has no upstream configured.
                var (fetchOk, fetchErr) = await FetchMinimalAsync(
                    repoPath, name, $"origin/{name}", bearerToken, ct, skipUpstreamCheck: true);
                if (!fetchOk)
                {
                    logger.LogWarning(
                        "Fetch before lease remote delete failed for {RepoPath}. Branch={Branch}, Error={Error}",
                        repoPath, name, fetchErr);
                    return (false, string.IsNullOrWhiteSpace(fetchErr)
                        ? "Could not fetch remote before deleting the branch."
                        : fetchErr);
                }

                var leaseSpec = $"refs/heads/{name}:{expectedSha.Trim()}";
                var leaseArgs = string.IsNullOrWhiteSpace(bearerToken)
                    ? $"{hooksPrefix}push --force-with-lease={leaseSpec} origin :{name}"
                    : $"{BuildAuthHeaderArgs(bearerToken)} {hooksPrefix}push --force-with-lease={leaseSpec} origin :{name}";
                var (leaseExit, leaseStdout, leaseStderr) = await runner.PushPipeline.ExecuteAsync(
                    async cancellationToken => await runner.RunAsync("git", leaseArgs, repoPath, cancellationToken),
                    ct);
                if (leaseExit != 0)
                {
                    var combined = CombineOutput(leaseStdout, leaseStderr) ?? "";
                    if (IsRemoteBranchAlreadyDeleted(combined))
                    {
                        logger.LogInformation("Git remote branch already deleted for {RepoPath}. Branch={Branch}", repoPath, name);
                        return (true, null);
                    }

                    if (IsForceWithLeaseRejected(combined))
                    {
                        logger.LogWarning(
                            "Git force-with-lease remote delete refused for {RepoPath}. Branch={Branch}",
                            repoPath, name);
                        return (false, "Remote branch tip no longer matches this Feature. Someone else may have pushed; remote branch was kept.");
                    }

                    logger.LogWarning(
                        "Git force-with-lease remote delete failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}",
                        repoPath, name, leaseExit);
                    return (false, combined);
                }

                logger.LogInformation("Git remote branch deleted with lease for {RepoPath}. Branch={Branch}", repoPath, name);
                return (true, null);
            }

            var args = string.IsNullOrWhiteSpace(bearerToken)
                ? $"{hooksPrefix}push origin --delete {name}"
                : $"{BuildAuthHeaderArgs(bearerToken)} {hooksPrefix}push origin --delete {name}";
            var (exitCode, stdout, stderr) = await runner.PushPipeline.ExecuteAsync(
                async cancellationToken => await runner.RunAsync("git", args, repoPath, cancellationToken),
                ct);
            if (exitCode != 0)
            {
                var combined = CombineOutput(stdout, stderr) ?? "";
                if (IsRemoteBranchAlreadyDeleted(combined))
                {
                    logger.LogInformation("Git remote branch already deleted for {RepoPath}. Branch={Branch}", repoPath, name);
                    return (true, null);
                }
                logger.LogWarning("Git push --delete failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, name, exitCode);
                return (false, combined);
            }
            logger.LogInformation("Git remote branch deleted for {RepoPath}. Branch={Branch}", repoPath, name);
            return (true, null);
        }

        var localName = branchName.Trim();
        if (localName.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            localName = localName.Substring("origin/".Length);
        if (string.IsNullOrWhiteSpace(localName))
            return (false, "Invalid branch name");

        if (!await RefExistsAsync(repoPath, $"refs/heads/{localName}", ct))
        {
            logger.LogWarning("Branch {Branch} does not exist in {RepoPath}", localName, repoPath);
            return (false, "Branch does not exist.");
        }

        var (exitCurrent, stdoutCurrent, _) = await runner.RunAsync("git", "branch --show-current", repoPath, ct);
        if (exitCurrent == 0 && (stdoutCurrent ?? "").Trim().Equals(localName, StringComparison.Ordinal))
        {
            logger.LogWarning("Cannot delete current branch {Branch} in {RepoPath}", localName, repoPath);
            return (false, "Cannot delete the current branch. Check out another branch first.");
        }

        if (force)
        {
            var (exitForce, stdoutForce, stderrForce) = await runner.RunAsync("git", $"branch -D {localName}", repoPath, ct);
            if (exitForce != 0)
            {
                logger.LogWarning("Git branch force delete failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, localName, exitForce);
                return (false, CombineOutput(stdoutForce, stderrForce));
            }
            logger.LogInformation("Git branch force deleted for {RepoPath}. Branch={Branch}", repoPath, localName);
            return (true, null);
        }

        var (exitCodeLocal, stdoutLocal, stderrLocal) = await runner.RunAsync("git", $"branch -d {localName}", repoPath, ct);
        if (exitCodeLocal != 0)
        {
            logger.LogWarning("Git branch delete failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, localName, exitCodeLocal);
            return (false, CombineOutput(stdoutLocal, stderrLocal));
        }

        logger.LogInformation("Git branch deleted for {RepoPath}. Branch={Branch}", repoPath, localName);
        return (true, null);
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

        var (exitCode, stdout, stderr) = await runner.RunAsync("git", "tag --sort=-creatordate", repoPath, ct);
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

    public async Task<(bool Success, string? ErrorMessage)> FetchTagsAsync(string repoPath, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (true, null);

        string args;
        if (string.IsNullOrWhiteSpace(bearerToken))
            args = "fetch origin --tags";
        else
            args = $"{BuildAuthHeaderArgs(bearerToken)} fetch origin --tags";

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.FetchPipeline.ExecuteAsync(
            async cancellationToken => await runner.RunAsync("git", args, repoPath, cancellationToken),
            ct);
        sw.Stop();
        if (exitCode != 0)
        {
            var combined = CombineOutput(stdout, stderr) ?? $"Git fetch tags failed (exit code {exitCode})";
            logger.LogWarning("Git fetch tags failed in {ElapsedMs}ms for {RepoPath}. ExitCode={ExitCode}", sw.ElapsedMilliseconds, repoPath, exitCode);
            return (false, combined);
        }
        logger.LogDebug("Git fetch tags completed in {ElapsedMs}ms for {RepoPath}", sw.ElapsedMilliseconds, repoPath);
        return (true, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> CheckoutTagAsync(string repoPath, string tagName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(tagName))
            return (false, "Invalid repository path or tag name");

        var name = tagName.Trim();
        if (!await RefExistsAsync(repoPath, $"refs/tags/{name}", ct))
            return (false, $"Tag '{name}' does not exist.");

        var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"-c advice.detachedHead=false checkout refs/tags/{name}", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogError("Git checkout tag failed for {RepoPath}. Tag={Tag}, ExitCode={ExitCode}", repoPath, name, exitCode);
            return (false, CombineOutput(stdout, stderr));
        }

        logger.LogInformation("Git checkout tag completed for {RepoPath}. Tag={Tag}", repoPath, name);
        return (true, null);
    }

    public async Task<string?> GetCheckedOutTagAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        var (symExit, _, _) = await runner.RunAsync("git", "symbolic-ref -q HEAD", repoPath, ct);
        if (symExit == 0)
            return null;

        var (descExit, stdout, _) = await runner.RunAsync("git", "describe --tags --exact-match", repoPath, ct);
        if (descExit != 0)
            return null;

        var tag = (stdout ?? "").Trim();
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    public Task<string?> GetDefaultBranchOriginRefAsync(string repoPath, CancellationToken ct)
        => GetDefaultBranchAsync(repoPath, ct);

    public string? ToOriginBranchRef(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return null;
        var trimmed = branchName.Trim();
        if (trimmed.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return $"origin/{trimmed}";
    }

    public async Task SetDivergenceBaseBranchAsync(string repoPath, string? divergenceBaseBranch, CancellationToken ct)
    {
        var path = await ResolveDivergenceBaseFilePathAsync(repoPath, ct);
        if (path is null)
            return;

        if (string.IsNullOrWhiteSpace(divergenceBaseBranch))
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        var name = divergenceBaseBranch.Trim();
        if (name.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            name = name["origin/".Length..];
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, name + "\n", ct);
    }

    public async Task<string?> GetDivergenceBaseBranchAsync(string repoPath, CancellationToken ct)
    {
        var path = await ResolveDivergenceBaseFilePathAsync(repoPath, ct);
        if (path is null || !File.Exists(path))
            return null;

        var text = (await File.ReadAllTextAsync(path, ct)).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Worktree-private file (not the common git dir) so Feature worktrees keep their own parent-branch base.
    /// </summary>
    private async Task<string?> ResolveDivergenceBaseFilePathAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        var (exitCode, stdout, _) = await runner.RunAsync(
            "git",
            "rev-parse --git-dir",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false,
            intent: GitLockIntent.Read);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            return null;

        var gitDir = stdout.Trim();
        var fullGitDir = Path.IsPathRooted(gitDir)
            ? gitDir
            : Path.GetFullPath(Path.Combine(repoPath, gitDir));

        return Path.Combine(fullGitDir, "graymoon-divergence-base");
    }

    private async Task<string?> GetDefaultBranchAsync(string repoPath, CancellationToken ct)
    {
        var (exitHead, stdoutHead, _) = await runner.RunAsync(
            "git",
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

    private async Task<string?> GetUpstreamRefAsync(string repoPath, string branchName, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await runner.RunAsync(
            "git",
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
    private async Task<bool> RefExistsAsync(string repoPath, string revision, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(revision))
            return false;

        var (exit, _, _) = await runner.RunAsync(
            "git",
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
    private async Task<string?> ResolveNoUpstreamCompareRefAsync(
        string repoPath,
        string? defaultBranchOriginRef,
        CancellationToken ct)
    {
        var divergenceBase = await GetDivergenceBaseBranchAsync(repoPath, ct);
        if (!string.IsNullOrWhiteSpace(divergenceBase))
        {
            var local = divergenceBase.Trim();
            if (local.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
                local = local["origin/".Length..];
            if (await RefExistsAsync(repoPath, local, ct))
                return local;
        }

        return defaultBranchOriginRef ?? await GetDefaultBranchAsync(repoPath, ct);
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
        var (exitDefault, stdoutDefault, stderrDefault) = await runner.RunAsync(
            "git",
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

    public async Task<(bool Success, bool Committed, string? ErrorMessage)> StageAndCommitAsync(string repoPath, IReadOnlyList<string> pathsToStage, string commitMessage, CancellationToken ct, bool skipHooks = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (false, false, "Invalid repository path");
        if (pathsToStage == null || pathsToStage.Count == 0)
            return (false, false, "No paths to stage");
        if (string.IsNullOrWhiteSpace(commitMessage))
            return (false, false, "Commit message is required");

        var paths = pathsToStage.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim().Replace('\\', '/')).Distinct().ToList();
        if (paths.Count == 0)
            return (false, false, "No paths to stage");

        var hooksArgs = GetHooksConfigArgs(skipHooks);
        var addPrefix = hooksArgs.Concat(["add"]).ToArray();
        var (addExit, addOut, addErr) = await GitIgnoredPathFilter.AddWithIgnoredFallbackAsync(
            runner,
            logger,
            repoPath,
            paths,
            remaining => RunPathspecOperationAsync(repoPath, addPrefix, remaining, ct),
            ct);
        if (addExit != 0)
        {
            var err = (addErr ?? addOut ?? "").Trim();
            logger.LogError("Git add failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}", repoPath, addExit, err);
            return (false, false, err);
        }

        var (stagedExit, _, stagedErr) = await runner.RunAsync("git", "diff --cached --quiet", repoPath, ct);
        if (stagedExit == 0)
        {
            logger.LogInformation("Git stage and commit skipped for {RepoPath}: nothing staged to commit", repoPath);
            return (true, false, null);
        }
        if (stagedExit != 1)
        {
            var err = (stagedErr ?? "").Trim();
            logger.LogError("Git staged diff check failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}", repoPath, stagedExit, err);
            return (false, false, string.IsNullOrWhiteSpace(err) ? "Failed to verify staged changes before commit." : err);
        }

        var messageNormalized = commitMessage.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
        var messageBytes = Encoding.UTF8.GetBytes(messageNormalized);
        var commitArgs = hooksArgs.Concat(["commit", "-F", "-"]).ToArray();
        var (commitExit, commitOut, commitErr) = await runner.RunAsync("git", commitArgs, repoPath, messageBytes, ct);
        if (commitExit != 0)
        {
            var err = (commitErr ?? commitOut ?? "").Trim();
            logger.LogError("Git commit failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}", repoPath, commitExit, err);
            return (false, false, err);
        }

        logger.LogInformation("Git stage and commit completed for {RepoPath}", repoPath);
        return (true, true, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> ResetToRemoteAsync(string repoPath, string branchName, bool keepChanges, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, "Invalid repository path or branch name");

        if (!await RefExistsAsync(repoPath, $"origin/{branchName}", ct))
        {
            logger.LogInformation("Remote ref origin/{BranchName} not found in {RepoPath} - pushing branch upstream first", branchName, repoPath);
            var (pushOk, pushErr) = await PushAsync(repoPath, branchName, bearerToken, setTracking: true, ct: ct);
            if (!pushOk)
                return (false, pushErr ?? "Failed to push branch upstream before reset");
        }

        var mode = keepChanges ? "--mixed" : "--hard";
        var target = $"origin/{branchName}";
        var args = $"reset {mode} {target}";

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.RunAsync("git", args, repoPath, ct);
        sw.Stop();
        if (exitCode != 0)
        {
            var combined = CombineOutput(stdout, stderr) ?? "";
            logger.LogError("Git reset failed in {ElapsedMs}ms for {RepoPath}. Args={Args}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}",
                sw.ElapsedMilliseconds, repoPath, args, exitCode, stdout, stderr);
            return (false, combined);
        }

        logger.LogInformation("Git reset {Mode} {Target} completed in {ElapsedMs}ms for {RepoPath}", mode, target, sw.ElapsedMilliseconds, repoPath);
        return (true, null);
    }

    public void CreateDirectory(string path)
    {
        if (Directory.Exists(path))
            return;
        Directory.CreateDirectory(path);
        logger.LogInformation("Created directory: {Path}", path);
    }

    public bool DirectoryExists(string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    public string[] GetDirectories(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return [];
        return Directory.GetDirectories(path).Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToArray();
    }

    private const string GrayMoonHookMarker = "# Created by GrayMoon.Agent";
    private const string ReplacedHookSuffix = ".replaced-by-graymoon";

    private sealed record GitHooksLocation(string? Directory, string? OutsideGitDirHooksPath);

    public async Task WriteSyncHooksAsync(string repoPath, int workspaceId, int repositoryId, CancellationToken ct)
    {
        try
        {
            await WriteSyncHooksCoreAsync(repoPath, workspaceId, repositoryId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not install sync hooks for {RepoPath} (workspace {WorkspaceId}, repo {RepositoryId}); live updates for changes made outside GrayMoon will appear only after the next Sync.",
                repoPath, workspaceId, repositoryId);
        }
    }

    private async Task WriteSyncHooksCoreAsync(string repoPath, int workspaceId, int repositoryId, CancellationToken ct)
    {
        var location = await ResolveGitHooksLocationAsync(repoPath, ct);
        if (location.Directory is null)
        {
            logger.LogWarning(
                "Could not resolve git hooks directory for {RepoPath} (workspace {WorkspaceId}, repo {RepositoryId}); skipping hook install.",
                repoPath, workspaceId, repositoryId);
            return;
        }

        if (location.OutsideGitDirHooksPath is not null)
        {
            logger.LogWarning(
                "Repository {RepoPath} sets core.hooksPath to '{HooksPath}', outside its Git directory. GrayMoon did not write or change any Git hooks there, so live updates for changes made outside GrayMoon will appear only after the next Sync.",
                repoPath, location.OutsideGitDirHooksPath);
            return;
        }

        var hooksDir = location.Directory;
        Directory.CreateDirectory(hooksDir);

        // Context-agnostic hooks: resolve the executing worktree root at runtime so linked
        // Feature worktrees attribute correctly. Do not embed a static Feature context id.
        var utf8 = new UTF8Encoding(false);
        var comment = $"{GrayMoonHookMarker} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n";
        var resolveBody =
            "REPO_PATH=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0\n" +
            "REPO_JSON=$(printf '%s' \"$REPO_PATH\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
            $"PAYLOAD=\"{{\\\"repositoryId\\\":{repositoryId},\\\"workspaceId\\\":{workspaceId},\\\"repositoryPath\\\":\\\"$REPO_JSON\\\"}}\"\n";
        var header = "-H \"Content-Type: application/json\"";
        var curlFlags = "-s --connect-timeout 1 --max-time 2";

        string Curl(string hookPath) =>
            $"curl {curlFlags} -X POST \"http://127.0.0.1:{_listenPort}/hook/{hookPath}\" {header} -d \"$PAYLOAD\" || true";

        InstallSyncHook(repoPath, hooksDir, "post-commit",
            "#!/bin/sh\n" + comment + resolveBody + Curl("commit") + "\n", utf8);
        InstallSyncHook(repoPath, hooksDir, "post-checkout",
            "#!/bin/sh\n" + comment + "[ \"$3\" = \"1\" ] || exit 0\n" + resolveBody + Curl("checkout") + "\n", utf8);
        InstallSyncHook(repoPath, hooksDir, "post-merge",
            "#!/bin/sh\n" + comment + resolveBody + Curl("merge") + "\n", utf8);
        InstallSyncHook(repoPath, hooksDir, "pre-push",
            "#!/bin/sh\n" + comment + resolveBody + Curl("push") + "\n", utf8);
        logger.LogDebug("Sync hooks checked for repo {RepoId} in workspace {WorkspaceId}", repositoryId, workspaceId);
    }

    private void InstallSyncHook(string repoPath, string hooksDir, string hookName, string content, Encoding encoding)
    {
        var path = Path.Combine(hooksDir, hookName);
        try
        {
            if (Directory.Exists(path))
            {
                logger.LogWarning(
                    "Cannot install the GrayMoon {Hook} hook for {RepoPath}: '{HookPath}' is a directory. Live updates for that Git event will appear only after the next Sync.",
                    hookName, repoPath, path);
                return;
            }

            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path);
                if (IsGrayMoonHook(existing))
                {
                    if (WithoutMarkerLine(existing) == WithoutMarkerLine(content))
                        return;
                }
                else
                {
                    string replacedPath;
                    try
                    {
                        replacedPath = MoveForeignHookAside(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(ex,
                            "Could not rename the existing Git hook '{Hook}' in {RepoPath}. GrayMoon left it untouched and did not install its own {Hook} hook, so live updates for that Git event will appear only after the next Sync.",
                            hookName, repoPath, hookName);
                        return;
                    }

                    logger.LogWarning(
                        "Repository {RepoPath} already had its own Git hook '{Hook}'. GrayMoon renamed it to '{ReplacedName}' and installed its own hook. The renamed hook no longer runs (Git only runs files named exactly like the hook); rename it back to restore it.",
                        repoPath, hookName, Path.GetFileName(replacedPath));
                }
            }

            WriteHookFile(path, content, encoding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex,
                "Could not install the GrayMoon {Hook} hook for {RepoPath}; live updates for that Git event will appear only after the next Sync.",
                hookName, repoPath);
        }
    }

    private static string MoveForeignHookAside(string path)
    {
        var replacedPath = path + ReplacedHookSuffix;
        if (File.Exists(replacedPath) || Directory.Exists(replacedPath))
            replacedPath = $"{path}{ReplacedHookSuffix}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(path, replacedPath);
        return replacedPath;
    }

    private static bool IsGrayMoonHook(string content)
    {
        using var reader = new StringReader(content);
        var line = reader.ReadLine();
        if (line is not null && line.StartsWith("#!", StringComparison.Ordinal))
            line = reader.ReadLine();
        while (line is not null && string.IsNullOrWhiteSpace(line))
            line = reader.ReadLine();
        return line is not null && line.StartsWith(GrayMoonHookMarker, StringComparison.Ordinal);
    }

    private static string WithoutMarkerLine(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        var markerIndex = lines.FindIndex(l => l.StartsWith(GrayMoonHookMarker, StringComparison.Ordinal));
        if (markerIndex >= 0)
            lines.RemoveAt(markerIndex);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Resolves the hooks directory that Git will actually execute for <paramref name="repoPath"/> via
    /// <c>git rev-parse --git-path hooks</c>, which honours <c>core.hooksPath</c>. For a linked worktree
    /// (a GrayMoon Feature or an external worktree) this is the common Git directory's <c>hooks</c> folder,
    /// shared by every linked worktree (see design doc &#167;13.1). When <c>core.hooksPath</c> is set and
    /// resolves outside the common Git directory (husky, lefthook, a shared folder), the configured value
    /// is returned in <see cref="GitHooksLocation.OutsideGitDirHooksPath"/> and nothing may be written there.
    /// </summary>
    private async Task<GitHooksLocation> ResolveGitHooksLocationAsync(string repoPath, CancellationToken ct)
    {
        var (hooksExit, hooksOut, _) = await runner.RunAsync(
            "git", ["rev-parse", "--git-path", "hooks"], repoPath, null, ct, GitLockIntent.Read);
        var hooksRaw = hooksOut?.Trim();
        if (hooksExit != 0 || string.IsNullOrWhiteSpace(hooksRaw))
            return new GitHooksLocation(null, null);

        var hooksDir = Path.GetFullPath(Path.Combine(repoPath, hooksRaw));

        var (configExit, configOut, _) = await runner.RunAsync(
            "git", ["config", "--get", "core.hooksPath"], repoPath, null, ct, GitLockIntent.Read);
        var configured = configExit == 0 ? configOut?.Trim() : null;
        if (string.IsNullOrEmpty(configured))
            return new GitHooksLocation(hooksDir, null);

        var (commonExit, commonOut, _) = await runner.RunAsync(
            "git", ["rev-parse", "--git-common-dir"], repoPath, null, ct, GitLockIntent.Read);
        var commonRaw = commonOut?.Trim();
        if (commonExit != 0 || string.IsNullOrWhiteSpace(commonRaw))
            return new GitHooksLocation(null, null);

        var commonDir = Path.GetFullPath(Path.Combine(repoPath, commonRaw));
        return IsPathInside(hooksDir, commonDir)
            ? new GitHooksLocation(hooksDir, null)
            : new GitHooksLocation(hooksDir, configured);
    }

    private static bool IsPathInside(string path, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var separator = Path.DirectorySeparatorChar;
        var normalizedParent = Path.TrimEndingDirectorySeparator(parent) + separator;
        var normalizedPath = Path.TrimEndingDirectorySeparator(path) + separator;
        return normalizedPath.StartsWith(normalizedParent, comparison);
    }

    public async Task<(bool Success, IReadOnlyList<GitWorktreeInfo> Worktrees, string? ErrorCode, string? ErrorMessage)> ListWorktreesAsync(
        string mainRepositoryPath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mainRepositoryPath) || !Directory.Exists(mainRepositoryPath))
            return (false, [], "RepositoryNotFound", "Repository not found.");

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            ["worktree", "list", "--porcelain"],
            mainRepositoryPath,
            null,
            ct,
            GitLockIntent.Read);

        if (exitCode != 0)
        {
            var error = CombineOutput(stdout, stderr) ?? "git worktree list failed";
            logger.LogError("Git worktree list failed for {RepoPath}. ExitCode={ExitCode}", mainRepositoryPath, exitCode);
            return (false, [], "GitFailed", error);
        }

        return (true, GitWorktreePorcelainParser.Parse(stdout), null, null);
    }

    public async Task<(bool Success, GitWorktreeInfo? Worktree, bool AlreadyExisted, string? ErrorCode, string? ErrorMessage)> CreateWorktreeAsync(
        string mainRepositoryPath,
        string worktreePath,
        string? branchName,
        string baseCommitSha,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mainRepositoryPath) || !Directory.Exists(mainRepositoryPath))
            return (false, null, false, "RepositoryNotFound", "Repository not found.");
        if (string.IsNullOrWhiteSpace(worktreePath))
            return (false, null, false, "InvalidWorktreePath", "worktreePath is required.");
        var detach = string.IsNullOrWhiteSpace(branchName);
        if (string.IsNullOrWhiteSpace(baseCommitSha))
            return (false, null, false, "InvalidBaseCommit", "baseCommitSha is required.");

        string canonicalWorktreePath;
        try
        {
            canonicalWorktreePath = Path.GetFullPath(worktreePath);
        }
        catch (Exception ex)
        {
            return (false, null, false, "InvalidWorktreePath", ex.Message);
        }

        var (listOk, worktrees, listCode, listError) = await ListWorktreesAsync(mainRepositoryPath, ct);
        if (!listOk)
            return (false, null, false, listCode, listError);

        var existingAtPath = GitWorktreeOccupancy.FindByPath(worktrees, canonicalWorktreePath);
        if (existingAtPath != null)
        {
            var matches = detach
                ? existingAtPath.IsDetached
                  && string.Equals(existingAtPath.HeadSha, baseCommitSha, StringComparison.OrdinalIgnoreCase)
                : GitWorktreeOccupancy.MatchesExpected(existingAtPath, branchName);
            if (matches)
            {
                logger.LogInformation(
                    "Worktree already exists at {WorktreePath} on {Branch}; treating create as idempotent success.",
                    canonicalWorktreePath, branchName ?? "(detached)");
                return (true, existingAtPath, true, null, null);
            }

            return (false, existingAtPath, false, "WorktreePathConflict",
                $"Path already hosts a worktree on branch '{existingAtPath.BranchName ?? "(detached)"}'.");
        }

        var branchOccupied = detach ? null : GitWorktreeOccupancy.FindByBranch(worktrees, branchName);
        if (branchOccupied != null)
        {
            return (false, branchOccupied, false, "BranchOccupied",
                $"Branch '{branchName}' is already checked out at '{branchOccupied.WorktreePath}'.");
        }

        if (File.Exists(canonicalWorktreePath))
        {
            return (false, null, false, "PathExists",
                $"Worktree path already exists on disk: {canonicalWorktreePath}");
        }

        // An existing, empty folder is allowed (D1): residue cleanup can legitimately leave an empty
        // worktree folder behind, and git worktree add works fine with an empty target directory.
        if (Directory.Exists(canonicalWorktreePath) && Directory.EnumerateFileSystemEntries(canonicalWorktreePath).Any())
        {
            return (false, null, false, "PathExists",
                $"Worktree path already exists on disk: {canonicalWorktreePath}");
        }

        var parent = Path.GetDirectoryName(canonicalWorktreePath);
        if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
            logger.LogInformation("Created worktree parent directory: {Path}", parent);
        }

        // Offline-safe: start from local commit SHA; never --force for normal creation.
        string[] addArgs = detach
            ? ["worktree", "add", "--detach", canonicalWorktreePath, baseCommitSha]
            : ["worktree", "add", "-b", branchName!, canonicalWorktreePath, baseCommitSha];
        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            addArgs,
            mainRepositoryPath,
            null,
            ct);

        if (exitCode != 0)
        {
            var error = CombineOutput(stdout, stderr) ?? "git worktree add failed";
            logger.LogError(
                "Git worktree add failed for {RepoPath}. Branch={Branch}, Path={WorktreePath}, ExitCode={ExitCode}",
                mainRepositoryPath, branchName, canonicalWorktreePath, exitCode);
            return (false, null, false, "GitFailed", error);
        }

        var (verifyOk, after, verifyCode, verifyError) = await ListWorktreesAsync(mainRepositoryPath, ct);
        if (!verifyOk)
            return (false, null, false, verifyCode, verifyError);

        var created = GitWorktreeOccupancy.FindByPath(after, canonicalWorktreePath)
            ?? GitWorktreeOccupancy.FindByBranch(after, branchName);
        if (created == null)
        {
            return (false, null, false, "VerifyFailed",
                "Worktree was created but could not be found in git worktree list.");
        }

        logger.LogInformation(
            "Git worktree created for {RepoPath}. Branch={Branch}, Path={WorktreePath}, Head={Head}",
            mainRepositoryPath, created.BranchName, created.WorktreePath, created.HeadSha);
        return (true, created, false, null, null);
    }

    public async Task<(bool Success, bool AlreadyRemoved, string? ErrorCode, string? ErrorMessage, WorktreeResidueResult Residue)> RemoveWorktreeAsync(
        string mainRepositoryPath,
        string worktreePath,
        bool force,
        CancellationToken ct,
        string? featureRootPath = null,
        string? featureStorageRoot = null,
        bool unlock = false)
    {
        if (string.IsNullOrWhiteSpace(mainRepositoryPath) || !Directory.Exists(mainRepositoryPath))
            return (false, false, "RepositoryNotFound", "Repository not found.", WorktreeResidueResult.None);
        if (string.IsNullOrWhiteSpace(worktreePath))
            return (false, false, "InvalidWorktreePath", "worktreePath is required.", WorktreeResidueResult.None);

        string canonicalWorktreePath;
        try
        {
            canonicalWorktreePath = Path.GetFullPath(worktreePath);
        }
        catch (Exception ex)
        {
            return (false, false, "InvalidWorktreePath", ex.Message, WorktreeResidueResult.None);
        }

        var (listOk, worktrees, listCode, listError) = await ListWorktreesAsync(mainRepositoryPath, ct);
        if (!listOk)
            return (false, false, listCode, listError, WorktreeResidueResult.None);

        var existing = GitWorktreeOccupancy.FindByPath(worktrees, canonicalWorktreePath);
        if (existing == null)
        {
            logger.LogInformation("Worktree {WorktreePath} already absent from inventory; treating remove as success.", canonicalWorktreePath);
            var residueWhenAlreadyGone = await RemoveWorktreeResidueAsync(
                mainRepositoryPath, canonicalWorktreePath, featureRootPath, featureStorageRoot, isRegisteredWorktree: false, ct);
            return (true, true, null, null, residueWhenAlreadyGone);
        }

        // Never remove the main (first / primary) worktree via this primitive.
        var primary = worktrees.FirstOrDefault(w => !w.IsBare && !string.IsNullOrWhiteSpace(w.WorktreePath));
        if (primary != null && GitWorktreeOccupancy.PathsEqual(primary.WorktreePath, canonicalWorktreePath))
        {
            return (false, false, "CannotRemovePrimary", "Cannot remove the primary repository worktree.", WorktreeResidueResult.None);
        }

        // D5: unlock is only authorized after explicit consent, surfaced in the Remove dialog when
        // InspectWorktree reports the worktree as locked. A failed unlock (for example it was not
        // actually locked) is logged and the remove below is attempted anyway, so git reports the
        // real reason for any remaining failure.
        if (unlock)
        {
            var (unlockExitCode, unlockStdout, unlockStderr) = await runner.RunAsync(
                "git", new[] { "worktree", "unlock", canonicalWorktreePath }, mainRepositoryPath, null, ct);
            if (unlockExitCode != 0)
            {
                logger.LogWarning(
                    "git worktree unlock failed for {WorktreePath}: {Error}",
                    canonicalWorktreePath, CombineOutput(unlockStdout, unlockStderr));
            }
        }

        var args = force
            ? new[] { "worktree", "remove", "--force", canonicalWorktreePath }
            : new[] { "worktree", "remove", canonicalWorktreePath };

        var (exitCode, stdout, stderr) = await runner.RunAsync("git", args, mainRepositoryPath, null, ct);
        var gitError = exitCode != 0 ? CombineOutput(stdout, stderr) ?? "git worktree remove failed" : null;

        var (verifyOk, after, verifyCode, verifyError) = await ListWorktreesAsync(mainRepositoryPath, ct);
        if (!verifyOk)
        {
            if (gitError != null)
                return (false, false, "GitFailed", gitError, WorktreeResidueResult.None);
            return (false, false, verifyCode, verifyError, WorktreeResidueResult.None);
        }

        if (GitWorktreeOccupancy.FindByPath(after, canonicalWorktreePath) != null)
        {
            // Still registered: git genuinely refused (for example dirty without force).
            logger.LogError(
                "Git worktree remove failed for {RepoPath}. Path={WorktreePath}, Force={Force}, ExitCode={ExitCode}",
                mainRepositoryPath, canonicalWorktreePath, force, exitCode);
            return gitError != null
                ? (false, false, "GitFailed", gitError, WorktreeResidueResult.None)
                : (false, false, "VerifyFailed", "Worktree remove reported success but path is still listed.", WorktreeResidueResult.None);
        }

        // Git unregistered the worktree either way. On Windows, deleting the directory itself can fail
        // (for example a file still open elsewhere) even though git already removed its own bookkeeping;
        // residue cleanup below retries the folder and reports the truth instead of a bare git error.
        if (gitError != null)
        {
            logger.LogWarning(
                "Git worktree remove exited {ExitCode} for {WorktreePath} but the worktree is unregistered; checking for leftover files. {Error}",
                exitCode, canonicalWorktreePath, gitError);
        }
        else
        {
            logger.LogInformation("Git worktree removed for {RepoPath}. Path={WorktreePath}, Force={Force}", mainRepositoryPath, canonicalWorktreePath, force);
        }

        var residue = await RemoveWorktreeResidueAsync(
            mainRepositoryPath, canonicalWorktreePath, featureRootPath, featureStorageRoot, isRegisteredWorktree: false, ct);
        return (true, false, null, null, residue);
    }

    /// <summary>
    /// After Git's own worktree removal, deletes any leftover files in <paramref name="worktreePath"/> with a
    /// custom walk (retries, reparse-point-safe) when every safety guard in
    /// <see cref="ValidateResidueRemovalGuards"/> passes, and reports anything left when it does not or when
    /// deletion could not finish (for example a file still open elsewhere). When <paramref name="featureRootPath"/>
    /// becomes empty afterward, it is removed too. Without <paramref name="featureRootPath"/> or
    /// <paramref name="featureStorageRoot"/>, nothing is deleted and only today's folder state is reported.
    /// </summary>
    private async Task<WorktreeResidueResult> RemoveWorktreeResidueAsync(
        string mainRepositoryPath,
        string worktreePath,
        string? featureRootPath,
        string? featureStorageRoot,
        bool isRegisteredWorktree,
        CancellationToken ct)
    {
        var guardFailure = ValidateResidueRemovalGuards(worktreePath, mainRepositoryPath, featureRootPath, featureStorageRoot, isRegisteredWorktree);

        if (!Directory.Exists(worktreePath))
        {
            if (guardFailure == null)
                TryRemoveEmptyFeatureRoot(featureRootPath);
            return WorktreeResidueResult.None;
        }

        if (guardFailure != null)
        {
            logger.LogWarning("Worktree residue cleanup skipped for {WorktreePath}: {Guard}", worktreePath, guardFailure);
            var (skippedCount, skippedSample) = ScanResidueFiles(worktreePath);
            return new WorktreeResidueResult(true, skippedCount, skippedSample, guardFailure);
        }

        await DeleteFolderRecursivelyWithRetryAsync(worktreePath, ct);

        if (Directory.Exists(worktreePath))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(worktreePath).Any())
                    Directory.Delete(worktreePath, false);
            }
            catch
            {
                // Best effort; the residue scan below reports the true state either way.
            }
        }

        if (Directory.Exists(worktreePath))
        {
            var (remainingCount, remainingSample) = ScanResidueFiles(worktreePath);
            var message = remainingCount > 0
                ? "Some files could not be deleted. They may still be open in another program."
                : "The worktree folder could not be removed.";
            logger.LogWarning("Worktree residue remains at {WorktreePath}: {Count} file(s). {Message}", worktreePath, remainingCount, message);
            return new WorktreeResidueResult(true, remainingCount, remainingSample, message);
        }

        TryRemoveEmptyFeatureRoot(featureRootPath);
        return WorktreeResidueResult.None;
    }

    /// <summary>
    /// Deletes <paramref name="featureRootPath"/> only when it exists and is empty. A reparse point at
    /// this level is never treated as empty content and is left alone (should not occur for a Feature root).
    /// </summary>
    private void TryRemoveEmptyFeatureRoot(string? featureRootPath)
    {
        if (string.IsNullOrWhiteSpace(featureRootPath) || !Directory.Exists(featureRootPath))
            return;

        try
        {
            if (!Directory.EnumerateFileSystemEntries(featureRootPath).Any())
            {
                Directory.Delete(featureRootPath, false);
                logger.LogInformation("Removed empty Feature root folder {FeatureRootPath}.", featureRootPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove Feature root folder {FeatureRootPath}.", featureRootPath);
        }
    }

    private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
    private static readonly int[] ResidueDeleteRetryDelaysMs = [200, 400, 800, 1600, 3200];

    /// <summary>
    /// Checks every safety guard before any residue deletion is allowed. Returns null when all guards pass,
    /// or a short description of the first guard that failed. Pure (no deletion); callers must still check
    /// disk state separately. Depth is checked before the Feature-root relationship so a path that is both
    /// too shallow and outside the root is reported as too shallow, matching the dedicated test for that guard.
    /// </summary>
    internal static string? ValidateResidueRemovalGuards(
        string worktreePath,
        string mainRepositoryPath,
        string? featureRootPath,
        string? featureStorageRoot,
        bool isRegisteredWorktree)
    {
        if (string.IsNullOrWhiteSpace(featureStorageRoot) || string.IsNullOrWhiteSpace(featureRootPath))
            return "No Feature storage root was provided; residue was only reported, not removed.";

        string normalizedWorktreePath;
        string normalizedMainRepositoryPath;
        string normalizedFeatureRootPath;
        string normalizedFeatureStorageRoot;
        try
        {
            normalizedWorktreePath = NormalizeResiduePath(worktreePath);
            normalizedMainRepositoryPath = NormalizeResiduePath(mainRepositoryPath);
            normalizedFeatureRootPath = NormalizeResiduePath(featureRootPath);
            normalizedFeatureStorageRoot = NormalizeResiduePath(featureStorageRoot);
        }
        catch (Exception ex)
        {
            return $"Could not resolve the Feature storage paths: {ex.Message}";
        }

        // Guard: at least 2 levels below featureStorageRoot (features\<FeatureName>\<Repo>).
        var relativeToStorageRoot = Path.GetRelativePath(normalizedFeatureStorageRoot, normalizedWorktreePath);
        var escapesStorageRoot = relativeToStorageRoot.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeToStorageRoot);
        if (!escapesStorageRoot)
        {
            var depthSegments = relativeToStorageRoot.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (depthSegments.Length < 2)
                return "Worktree path is too shallow under the Feature storage root.";
        }

        // Guard: worktreePath is strictly under featureStorageRoot\<FeatureName>\, and featureRootPath
        // equals featureStorageRoot\<FeatureName>. Worktrees under the legacy drive-root path fail here.
        var featureRootParent = Path.GetDirectoryName(normalizedFeatureRootPath);
        if (!string.Equals(featureRootParent, normalizedFeatureStorageRoot, StringComparison.OrdinalIgnoreCase)
            || !IsStrictlyUnderResiduePath(normalizedWorktreePath, normalizedFeatureRootPath))
        {
            return "Worktree path is not under this Feature's storage root.";
        }

        // Guard: never the primary repository checkout, and never a path that contains it.
        if (string.Equals(normalizedWorktreePath, normalizedMainRepositoryPath, StringComparison.OrdinalIgnoreCase)
            || IsStrictlyUnderResiduePath(normalizedMainRepositoryPath, normalizedWorktreePath))
        {
            return "Worktree path is or contains the primary repository checkout.";
        }

        // Guard: must not currently be a registered worktree.
        if (isRegisteredWorktree)
            return "Path is still a registered Git worktree.";

        // Guard: a real repository has a .git directory at its top level; a linked worktree does not.
        if (Directory.Exists(Path.Combine(normalizedWorktreePath, ".git")))
            return "Path contains a .git directory and looks like a real repository, not a linked worktree.";

        return null;
    }

    private static string NormalizeResiduePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsStrictlyUnderResiduePath(string path, string potentialAncestor)
    {
        var prefix = potentialAncestor + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Counts files under <paramref name="folderPath"/> (never entering a reparse point) and samples up to 5 relative paths. No deletion.</summary>
    private static (int Count, List<string> Sample) ScanResidueFiles(string folderPath)
    {
        var count = 0;
        var sample = new List<string>();
        VisitResidueEntries(folderPath, entry =>
        {
            count++;
            if (sample.Count < 5)
                sample.Add(Path.GetRelativePath(folderPath, entry.FullName));
        });
        return (count, sample);
    }

    private static void VisitResidueEntries(string folderPath, Action<FileSystemInfo> onFileOrLink)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(folderPath).EnumerateFileSystemInfos();
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            var isReparsePoint = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (entry is DirectoryInfo && !isReparsePoint)
            {
                VisitResidueEntries(entry.FullName, onFileOrLink);
                continue;
            }

            // A file, or a reparse point (junction/symlink): never enter the link, only count/report it.
            onFileOrLink(entry);
        }
    }

    /// <summary>
    /// Deletes everything under <paramref name="folderPath"/> with a custom walk (never
    /// <c>Directory.Delete(path, true)</c>): clears read-only attributes, deletes reparse points
    /// (junctions/symlinks) as the link itself without entering them, and retries each entry up to 5
    /// times (200, 400, 800, 1600, 3200 ms) on <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> (for example a file still open in another program).
    /// Leaves whatever it could not delete in place; the caller reports that as residue.
    /// </summary>
    private static async Task DeleteFolderRecursivelyWithRetryAsync(string folderPath, CancellationToken ct)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(folderPath).EnumerateFileSystemInfos().ToList();
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var isReparsePoint = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (entry is DirectoryInfo && !isReparsePoint)
                await DeleteFolderRecursivelyWithRetryAsync(entry.FullName, ct);

            await DeleteResidueEntryWithRetryAsync(entry, ct);
        }
    }

    private static async Task DeleteResidueEntryWithRetryAsync(FileSystemInfo entry, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= ResidueDeleteRetryDelaysMs.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                    entry.Attributes &= ~FileAttributes.ReadOnly;

                if (entry is DirectoryInfo directory)
                {
                    // Delete the entry itself only (reparse point: the link; otherwise an already-emptied directory).
                    directory.Delete(false);
                }
                else
                {
                    entry.Delete();
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == ResidueDeleteRetryDelaysMs.Length)
                    return;
                await Task.Delay(ResidueDeleteRetryDelaysMs[attempt], ct);
            }
            catch
            {
                return;
            }
        }
    }

    public async Task<WorktreeInspectionResult> InspectWorktreeAsync(
        string mainRepositoryPath,
        string worktreePath,
        string? defaultBranch,
        string? featureBranch,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mainRepositoryPath) || !Directory.Exists(mainRepositoryPath))
            return new WorktreeInspectionResult(false, false, false, null, null, null, null, null, null, null, null, null, null, null, null, "Repository not found.");
        if (string.IsNullOrWhiteSpace(worktreePath))
            return new WorktreeInspectionResult(false, false, false, null, null, null, null, null, null, null, null, null, null, null, null, "worktreePath is required.");

        string canonicalWorktreePath;
        try
        {
            canonicalWorktreePath = Path.GetFullPath(worktreePath);
        }
        catch (Exception ex)
        {
            return new WorktreeInspectionResult(false, false, false, null, null, null, null, null, null, null, null, null, null, null, null, ex.Message);
        }

        var (listOk, worktrees, _, listError) = await ListWorktreesAsync(mainRepositoryPath, ct);
        if (!listOk)
            return new WorktreeInspectionResult(false, false, false, null, null, null, null, null, null, null, null, null, null, null, null, listError ?? "Failed to list worktrees.");

        var registered = GitWorktreeOccupancy.FindByPath(worktrees, canonicalWorktreePath);
        var isRegistered = registered != null;
        var isLocked = registered?.IsLocked ?? false;
        var lockReason = registered?.LockReason;

        if (!Directory.Exists(canonicalWorktreePath))
        {
            return new WorktreeInspectionResult(isRegistered, false, isLocked, lockReason, null, null, null, null, null, null, null, null, null, null, null, null);
        }

        var headSha = await GetHeadCommitAsync(canonicalWorktreePath, ct);
        var branch = await GetCurrentBranchNameAsync(canonicalWorktreePath, ct);

        var (statusOk, staged, unstaged, untracked, conflicts, statusError) = await ProbeWorktreeStatusAsync(canonicalWorktreePath, ct);
        bool? isDirty = statusOk ? staged + unstaged + untracked + conflicts > 0 : null;

        bool? hasUpstream = null;
        int? aheadOfUpstream = null;
        int? behindUpstream = null;
        if (!string.IsNullOrWhiteSpace(branch))
        {
            var (upstreamKnown, ahead, behind) = await ProbeUpstreamCountsAsync(canonicalWorktreePath, branch, ct);
            hasUpstream = upstreamKnown;
            aheadOfUpstream = ahead;
            behindUpstream = behind;
        }

        // A Feature worktree carries its own persisted "divergence base" (the branch it was actually
        // created from - its parent, which can itself be another unmerged, not-yet-pushed Feature
        // branch, not necessarily the repository's true default branch). "Ahead of default" must be
        // judged against that parent when one was recorded, or deleting a nested Feature branch
        // would be reported as losing every commit the parent branch is already ahead of main by,
        // even though those commits stay reachable from the parent branch and are never actually
        // lost. Falls back to the repository's true default branch when no divergence base was
        // recorded, or it no longer resolves to an existing ref.
        var aheadOfDefaultCompareRef = await ResolveAheadOfDefaultCompareRefAsync(canonicalWorktreePath, defaultBranch, ct);

        var aheadOfDefault = await ProbeAheadOfDefaultAsync(canonicalWorktreePath, aheadOfDefaultCompareRef, ct);

        bool? featureBranchExists = null;
        string? featureBranchSha = null;
        int? featureBranchAheadOfDefault = null;
        bool? featureBranchHasUpstream = null;
        int? featureBranchAheadOfUpstream = null;
        if (!string.IsNullOrWhiteSpace(featureBranch))
        {
            featureBranchSha = await GetRevisionShaAsync(canonicalWorktreePath, $"refs/heads/{featureBranch}", ct);
            featureBranchExists = featureBranchSha != null;
            if (featureBranchExists == true)
            {
                featureBranchAheadOfDefault = await ProbeAheadOfDefaultForRefAsync(
                    canonicalWorktreePath, aheadOfDefaultCompareRef, $"refs/heads/{featureBranch}", ct);
                var (featureUpstreamKnown, featureAhead) = await ProbeFeatureBranchUpstreamCountAsync(
                    canonicalWorktreePath, featureBranch, ct);
                featureBranchHasUpstream = featureUpstreamKnown;
                featureBranchAheadOfUpstream = featureAhead;
            }
        }

        return new WorktreeInspectionResult(
            isRegistered,
            true,
            isLocked,
            lockReason,
            headSha,
            branch,
            isDirty,
            statusOk ? staged : null,
            statusOk ? unstaged : null,
            statusOk ? untracked : null,
            statusOk ? conflicts : null,
            hasUpstream,
            aheadOfUpstream,
            behindUpstream,
            aheadOfDefault,
            statusOk ? null : statusError,
            featureBranchExists,
            featureBranchSha,
            featureBranchAheadOfDefault,
            featureBranchHasUpstream,
            featureBranchAheadOfUpstream);
    }

    /// <summary>
    /// Resolves the ref to count "ahead of default" against: this worktree's own persisted
    /// divergence base (<see cref="GetDivergenceBaseBranchAsync"/>, the Feature's actual parent
    /// branch) when one was recorded and still exists - checked as a local branch name first since a
    /// parent that is itself an unmerged, unpushed Feature branch never has an <c>origin/</c> ref -
    /// falling back to <see cref="ToOriginBranchRef"/> of <paramref name="defaultBranch"/> otherwise
    /// (same resolution order as <see cref="ResolveNoUpstreamCompareRefAsync"/>/GetCommitCountsCommand).
    /// </summary>
    private async Task<string?> ResolveAheadOfDefaultCompareRefAsync(string repoPath, string? defaultBranch, CancellationToken ct)
    {
        var divergenceBase = await GetDivergenceBaseBranchAsync(repoPath, ct);
        if (!string.IsNullOrWhiteSpace(divergenceBase))
        {
            var local = divergenceBase.Trim();
            if (local.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
                local = local["origin/".Length..];
            if (await RefExistsAsync(repoPath, local, ct))
                return local;

            var originRef = $"origin/{local}";
            if (await RefExistsAsync(repoPath, originRef, ct))
                return originRef;
        }

        return ToOriginBranchRef(defaultBranch);
    }

    /// <summary>
    /// Like <see cref="ProbeAheadOfDefaultAsync"/> but against an arbitrary ref instead of always
    /// HEAD, so a Feature branch can be judged without checking it out (09 SB-2, plan unit I1).
    /// </summary>
    private async Task<int?> ProbeAheadOfDefaultForRefAsync(string repoPath, string? defaultRef, string compareRef, CancellationToken ct)
    {
        if (defaultRef == null || !await RefExistsAsync(repoPath, defaultRef, ct))
            return null;

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            $"rev-list --count {defaultRef}..{compareRef}",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitCode != 0)
        {
            logger.LogWarning("Git rev-list (InspectWorktree Feature branch ahead of default) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return null;
        }

        return int.TryParse((stdout ?? "").Trim(), out var count) ? count : (int?)null;
    }

    /// <summary>
    /// Like <see cref="ProbeUpstreamCountsAsync"/> but for an arbitrary local branch instead of
    /// always HEAD, so a Feature branch's upstream state can be judged without checking it out
    /// (09 SB-2, plan unit I1). Behind-count is not needed by any caller, so it is not computed.
    /// </summary>
    private async Task<(bool HasUpstream, int? Ahead)> ProbeFeatureBranchUpstreamCountAsync(string repoPath, string branchName, CancellationToken ct)
    {
        var upstreamRef = await GetUpstreamRefAsync(repoPath, branchName, ct);
        if (string.IsNullOrWhiteSpace(upstreamRef) || !await RefExistsAsync(repoPath, upstreamRef, ct))
            return (false, null);

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            $"rev-list --left-right --count {upstreamRef}...refs/heads/{branchName}",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitCode != 0)
        {
            logger.LogWarning("Git rev-list --left-right (InspectWorktree Feature branch upstream count) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return (true, null);
        }

        var parts = (stdout ?? "").Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
        var ahead = parts.Length >= 2 && int.TryParse(parts[1], out var a) ? a : (int?)null;
        return (true, ahead);
    }

    /// <summary>
    /// SHA of a revision if it exists, or null. Uses <c>rev-parse --verify --quiet</c> so a missing
    /// ref is a silent non-zero exit instead of a visible Git error (same reasoning as <see cref="RefExistsAsync"/>).
    /// </summary>
    private async Task<string?> GetRevisionShaAsync(string repoPath, string revision, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await runner.RunAsync(
            "git",
            $"rev-parse --verify --quiet {revision}",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitCode != 0)
            return null;

        var sha = (stdout ?? "").Trim();
        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    private async Task<(bool Success, int Staged, int Unstaged, int Untracked, int Conflicts, string? Error)> ProbeWorktreeStatusAsync(
        string repoPath,
        CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            "--no-optional-locks status --porcelain=v1",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false,
            intent: GitLockIntent.Read);
        if (exitCode != 0)
            return (false, 0, 0, 0, 0, CombineOutput(stdout, stderr) ?? "git status failed");

        var staged = 0;
        var unstaged = 0;
        var untracked = 0;
        var conflicts = 0;
        foreach (var line in (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 2)
                continue;
            var x = line[0];
            var y = line[1];
            if (x == '?' && y == '?')
            {
                untracked++;
                continue;
            }
            if (IsUnmergedStatusCode(x, y))
            {
                conflicts++;
                continue;
            }
            if (x != ' ')
                staged++;
            if (y != ' ')
                unstaged++;
        }

        return (true, staged, unstaged, untracked, conflicts, null);
    }

    private static bool IsUnmergedStatusCode(char x, char y)
        => x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D');

    /// <summary>Ahead/behind strictly against the branch's configured upstream; no fallback to the default branch
    /// or a Feature divergence base, unlike <see cref="ProbeCommitCountsAsync"/>, so the InspectWorktree caller
    /// gets a clean null when there is no upstream instead of a value computed against something else.</summary>
    private async Task<(bool HasUpstream, int? Ahead, int? Behind)> ProbeUpstreamCountsAsync(
        string repoPath,
        string branchName,
        CancellationToken ct)
    {
        var upstreamRef = await GetUpstreamRefAsync(repoPath, branchName, ct);
        if (string.IsNullOrWhiteSpace(upstreamRef) || !await RefExistsAsync(repoPath, upstreamRef, ct))
            return (false, null, null);

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            $"rev-list --left-right --count {upstreamRef}...HEAD",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitCode != 0)
        {
            logger.LogWarning("Git rev-list --left-right (InspectWorktree upstream counts) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return (true, null, null);
        }

        var parts = (stdout ?? "").Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
        var behind = parts.Length >= 1 && int.TryParse(parts[0], out var b) ? b : (int?)null;
        var ahead = parts.Length >= 2 && int.TryParse(parts[1], out var a) ? a : (int?)null;
        return (true, ahead, behind);
    }

    private async Task<int?> ProbeAheadOfDefaultAsync(string repoPath, string? defaultRef, CancellationToken ct)
    {
        if (defaultRef == null || !await RefExistsAsync(repoPath, defaultRef, ct))
            return null;

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            $"rev-list --count {defaultRef}..HEAD",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitCode != 0)
        {
            logger.LogWarning("Git rev-list (InspectWorktree ahead of default) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return null;
        }

        return int.TryParse((stdout ?? "").Trim(), out var count) ? count : (int?)null;
    }

    private static void WriteHookFile(string path, string content, Encoding encoding)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        File.WriteAllText(path, normalized, encoding);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // TODO: Harden and centralize GrayMoon Git remote authentication.
    // Audit all remote Git operations (clone, fetch, pull, push, remote delete, ls-remote, and future
    // remote operations) and ensure connector authentication is applied consistently from one
    // well-defined layer. Review non-interactive credential behavior, authentication-error
    // classification, safe logging/redaction, and fresh-machine behavior for private repositories.
    // Reduce the possibility that an individual caller can accidentally omit authentication.
    // This is intentionally deferred so the immediate private-repository fix remains minimal and
    // easy to transfer between branches.
    //
    // --- Audit (2026-09-26): remote ops vs connector token on a fresh machine (no GCM cache) ---
    // GitService network APIs already accept bearerToken and apply BuildAuthHeaderArgs when present:
    //   CloneAsync, FetchAsync, FetchMinimalAsync, PullAsync, PushAsync, GetRemoteBranchesAsync
    //   (ls-remote), FetchTagsAsync, DeleteBranchAsync (remote), ResetToRemoteAsync (conditional push).
    // Callers that DO pass a token today (via request.BearerToken or IAgentTokenProvider):
    //   SyncRepository, FetchCommits, ReturnToDefaultBranch (incl. remote delete),
    //   DeleteBranch (remote; Switch Branch modal / API via WorkspaceBranchOperations),
    //   UpdateBranchFromDefault, PushRepository, CommitSyncRepository, UndoPush,
    //   GetBranches, RefreshBranches, SetUpstreamBranch, CreateBranch (minimal fetch),
    //   CheckoutHookSync (minimal fetch + fetch tags), CommitHookSync (ls-remote),
    //   RefreshRepositoryVersion (ls-remote when needed).
    // Local DeleteBranch is fine (no network).
    // Remaining gap off this branch: WorkspaceFeatureOperations remote-branch cleanup (when that
    // code is present) must also send bearerToken on the DeleteBranch payload.
    // Note: if the connector token itself is missing/null, authenticated callers still fail; that is
    // connector configuration, not a propagation bug. GIT_TERMINAL_PROMPT=0 makes those fail fast.
    private static string BuildAuthHeaderArgs(string bearerToken)
    {
        var credentials = "x-access-token:" + bearerToken;
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        var headerValue = "Authorization: Basic " + base64;
        var escaped = headerValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"-c core.askpass=true -c credential.helper= -c \"http.extraHeader={escaped}\"";
    }

    private static string BuildCloneArguments(string cloneUrl, string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            return $"clone \"{cloneUrl}\"";
        return $"{BuildAuthHeaderArgs(bearerToken)} clone \"{cloneUrl}\"";
    }

    private static string SanitizeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "workspace";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Trim().Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "workspace" : sanitized;
    }

    private static string? CombineOutput(string? stdout, string? stderr)
    {
        var outStr = (stdout ?? "").Trim();
        var errStr = (stderr ?? "").Trim();
        if (string.IsNullOrWhiteSpace(outStr) && string.IsNullOrWhiteSpace(errStr))
            return null;
        return string.IsNullOrWhiteSpace(outStr) ? errStr
             : string.IsNullOrWhiteSpace(errStr) ? outStr
             : $"{outStr}\n{errStr}";
    }

    private static string BuildProcessError(string? stderr, string? stdout, string fallback)
        => (!string.IsNullOrWhiteSpace(stderr) ? stderr : stdout)?.Trim() ?? fallback;

    private static bool ShouldAttemptDotNetToolRestore(string fileName, string? stderr, string? stdout)
    {
        if (!string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
            return false;
        var output = string.Concat(stderr ?? string.Empty, "\n", stdout ?? string.Empty);
        if (string.IsNullOrWhiteSpace(output))
            return false;
        return output.Contains("could not execute because the specified command or file was not found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("dotnet-gitversion does not exist", StringComparison.OrdinalIgnoreCase)
            || output.Contains("no executable found matching command", StringComparison.OrdinalIgnoreCase)
            || output.Contains("is not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase)
            || output.Contains("run 'dotnet tool restore'", StringComparison.OrdinalIgnoreCase)
            || output.Contains("run \"dotnet tool restore\"", StringComparison.OrdinalIgnoreCase);
    }

    private static string EmptyHooksPath =>
        _emptyHooksPath ??= CreateEmptyHooksPath();

    private static string CreateEmptyHooksPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "GrayMoon-empty-hooks");
        Directory.CreateDirectory(path);
        return path.Replace('\\', '/');
    }

    private static string GetHooksConfigPrefix(bool skipHooks)
    {
        if (!skipHooks)
            return string.Empty;
        var escaped = EmptyHooksPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"-c core.hooksPath=\"{escaped}\" ";
    }

    private static List<string> GetHooksConfigArgs(bool skipHooks)
    {
        if (!skipHooks)
            return [];
        return ["-c", $"core.hooksPath={EmptyHooksPath}"];
    }

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> RunPathspecOperationAsync(
        string repoPath,
        IReadOnlyList<string> commandPrefix,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var stdinBytes = GitPathspecStdinWriter.BuildNulDelimitedUtf8(paths);
        var args = commandPrefix.Concat(["--pathspec-from-file=-", "--pathspec-file-nul"]).ToArray();
        var result = await runner.RunAsync("git", args, repoPath, stdinBytes, cancellationToken);
        if (result.ExitCode == 0)
            return result;

        if (!IsUnknownOptionError(result.Stderr, result.Stdout))
            return result;

        return await RunBoundedPathspecBatchesAsync(repoPath, commandPrefix, paths, cancellationToken);
    }

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> RunBoundedPathspecBatchesAsync(
        string repoPath,
        IReadOnlyList<string> commandPrefix,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var batches = GitPathspecStdinWriter.BuildBoundedBatches(paths);
        for (var i = 0; i < batches.Count; i++)
        {
            var args = commandPrefix.Concat(["--"]).Concat(batches[i]).ToArray();
            var result = await runner.RunAsync("git", args, repoPath, null, cancellationToken);
            if (result.ExitCode != 0)
            {
                var error = $"Batch {i + 1} of {batches.Count} failed: {(result.Stderr ?? result.Stdout ?? "git failed").Trim()}";
                return (result.ExitCode, result.Stdout, error);
            }
        }

        return (0, null, null);
    }

    private static bool IsUnknownOptionError(string? stderr, string? stdout)
    {
        var text = (stderr ?? string.Empty) + " " + (stdout ?? string.Empty);
        return text.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unrecognized argument", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRemoteBranchAlreadyDeleted(string output)
        => output.Contains("remote ref does not exist", StringComparison.OrdinalIgnoreCase)
        || (output.Contains("unable to delete", StringComparison.OrdinalIgnoreCase)
            && output.Contains("does not exist", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when git refused a force-with-lease push because the remote tip moved (D4).</summary>
    private static bool IsForceWithLeaseRejected(string output)
        => output.Contains("stale info", StringComparison.OrdinalIgnoreCase)
        || output.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            && output.Contains("force-with-lease", StringComparison.OrdinalIgnoreCase)
        || (output.Contains("failed to push some refs", StringComparison.OrdinalIgnoreCase)
            && (output.Contains("force-with-lease", StringComparison.OrdinalIgnoreCase)
                || output.Contains("but expected", StringComparison.OrdinalIgnoreCase)));
}
