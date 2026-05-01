using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Models;
using GrayMoon.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace GrayMoon.Agent.Services;

public sealed class GitService(IOptions<AgentOptions> options, ILogger<GitService> logger, ICommandLineService commandLine) : IGitService
{
    private readonly int _listenPort = options.Value.ListenPort;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

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
        var (exitCode, stdout, stderr) = await CloneRetryPipeline.ExecuteAsync(
            async (cancellationToken) => await RunProcessAsync("git", args, workingDir, cancellationToken),
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
        var pathForGit = fullPath.Replace('\\', '/'); // Git accepts forward slashes on all platforms

        var (isSafe, _) = await CheckRepoSafeAsync(repoPath, pathForGit, ct);
        logger.LogDebug("Git repo safety check: {Path} -> {Result}", pathForGit, isSafe ? "safe" : "not safe");

        if (isSafe)
        {
            logger.LogDebug("Repository already safe, skipping safe.directory update: {Path}", pathForGit);
            return;
        }

        var addArgs = $"config --local --add safe.directory \"{pathForGit.Replace("\"", "\\\"")}\"";
        var (exitCode, stdout, stderr) = await SafeDirectoryRetryPipeline.ExecuteAsync(
            async (cancellationToken) => await RunProcessAsync("git", addArgs, repoPath, cancellationToken),
            ct);

        if (exitCode == 0)
            logger.LogDebug("Added safe.directory for repository: {Path}", pathForGit);
        else
            logger.LogError("Git config safe.directory failed. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", exitCode, stdout, stderr);
    }

    /// <summary>
    /// Uses git rev-parse --is-inside-work-tree: exit 0 = repo is safe, exit 128 with dubious ownership = not safe.
    /// </summary>
    private async Task<(bool IsSafe, bool IsDubiousOwnership)> CheckRepoSafeAsync(string repoPath, string pathForGit, CancellationToken ct)
    {
        var (exitCode, _, stderr) = await RunProcessAsync("git", "rev-parse --is-inside-work-tree", repoPath, ct);
        if (exitCode == 0)
            return (true, false);
        var err = stderr ?? "";
        var isDubious = exitCode == 128 && (err.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase) || err.Contains("safe.directory", StringComparison.OrdinalIgnoreCase));
        return (false, isDubious);
    }

    private readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> SafeDirectoryRetryPipeline =
        GitResiliencePipelines.CreateSafeDirectoryPipeline(logger);

    private readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> CloneRetryPipeline =
        GitResiliencePipelines.CreateClonePipeline(logger);

    private readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> FetchRetryPipeline =
        GitResiliencePipelines.CreateFetchPipeline(logger);

    private readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> PullRetryPipeline =
        GitResiliencePipelines.CreatePullPipeline(logger);

    private readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> PushRetryPipeline =
        GitResiliencePipelines.CreatePushPipeline(logger);

    private readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> LsRemoteRetryPipeline =
        GitResiliencePipelines.CreateLsRemotePipeline(logger);

    private readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> MinimalFetchRetryPipeline =
        GitResiliencePipelines.CreateMinimalFetchPipeline(logger);

    public async Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, CancellationToken ct)
        => await GetVersionAsync(repoPath, nonNormalize: false, ct);

    public async Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (null, null);

        var (fileName, arguments) = GetGitVersionInvocation(repoPath, nonNormalize);
        var toolName = fileName == "dotnet" ? "dotnet gitversion" : "dotnet-gitversion";

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await RunProcessAsync(fileName, arguments, repoPath, ct);
        sw.Stop();
        if (exitCode != 0)
        {
            var manifestExists = File.Exists(Path.Combine(repoPath, "dotnet-tools.json"))
                || File.Exists(Path.Combine(repoPath, ".config", "dotnet-tools.json"));
            if (manifestExists)
            {
                logger.LogWarning("{ToolName} failed and tool manifest found. Running 'dotnet tool restore' in {RepoPath}", toolName, repoPath);
                var (restoreExitCode, _, restoreStderr) = await RunProcessAsync("dotnet", "tool restore", repoPath, ct);
                if (restoreExitCode != 0)
                {
                    logger.LogError("dotnet tool restore failed. ExitCode={ExitCode}, Stderr={Stderr}", restoreExitCode, restoreStderr);
                    return (null, $"dotnet tool restore failed: {restoreStderr?.Trim()}");
                }
                logger.LogInformation("dotnet tool restore succeeded in {RepoPath}. Retrying {ToolName}", repoPath, toolName);
                var (retryExitCode, retryStdout, retryStderr) = await RunProcessAsync(fileName, arguments, repoPath, ct);
                if (retryExitCode != 0)
                {
                    var retryError = (!string.IsNullOrWhiteSpace(retryStderr) ? retryStderr : retryStdout)?.Trim()
                                    ?? $"{toolName} exited with code {retryExitCode}";
                    logger.LogError("{ToolName} failed after tool restore. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", toolName, retryExitCode, retryStdout, retryStderr);
                    return (null, retryError);
                }
                stdout = retryStdout;
            }
            else
            {
                var error = (!string.IsNullOrWhiteSpace(stderr) ? stderr : stdout)?.Trim()
                            ?? $"{toolName} exited with code {exitCode}";
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
            return (null, $"Failed to parse {toolName} output: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns (fileName, arguments) for running GitVersion. Uses "dotnet gitversion" when a tool manifest
    /// (dotnet-tools.json) exists in the repo root or in .config; otherwise uses "dotnet-gitversion".
    /// Always passes /output json, /nofetch and /verbosity quiet; when <paramref name="nonNormalize"/> is true, also passes /nonormalize.
    /// </summary>
    private static (string FileName, string Arguments) GetGitVersionInvocation(string repoPath, bool nonNormalize)
    {
        const string manifestFileName = "dotnet-tools.json";
        var inRoot = Path.Combine(repoPath, manifestFileName);
        var inConfig = Path.Combine(repoPath, ".config", manifestFileName);
        var commonArgs = "/output json /nofetch /verbosity quiet";
        if (nonNormalize)
            commonArgs += " /nonormalize";
        if (File.Exists(inRoot) || File.Exists(inConfig))
            return ("dotnet", "gitversion " + commonArgs);
        return ("dotnet-gitversion", commonArgs);
    }

    public async Task<string?> GetCurrentBranchNameAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        var (exitCode, stdout, _) = await RunProcessAsync("git", "branch --show-current", repoPath, ct);
        if (exitCode != 0)
            return null;

        var name = (stdout ?? "").Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public async Task<string?> GetRemoteOriginUrlAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        var (exitCode, stdout, stderr) = await RunProcessAsync("git", "config --get remote.origin.url", repoPath, ct);
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
            // Use --prune to remove stale remote-tracking branches
            args = includeTags ? "fetch origin --prune --tags" : "fetch origin --prune";
            logArgs = args;
        }
        else
        {
            var credentials = "x-access-token:" + bearerToken;
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            var headerValue = "Authorization: Basic " + base64;
            var escaped = headerValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
            // Use --prune to remove stale remote-tracking branches
            var fetchCmd = includeTags ? "fetch origin --prune --tags" : "fetch origin --prune";
            args = $"-c core.askpass=true -c credential.helper= -c \"http.extraHeader={escaped}\" {fetchCmd}";
            logArgs = "***";
        }
        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await FetchRetryPipeline.ExecuteAsync(
            async cancellationToken => await RunProcessAsync("git", args, repoPath, cancellationToken),
            ct);
        sw.Stop();
        if (exitCode != 0)
        {
            var output = (stdout ?? "").Trim();
            var errorOutput = (stderr ?? "").Trim();
            var combinedOutput = string.IsNullOrWhiteSpace(output) ? errorOutput :
                                 string.IsNullOrWhiteSpace(errorOutput) ? output :
                                 $"{output}\n{errorOutput}";
            if (string.IsNullOrWhiteSpace(combinedOutput))
                combinedOutput = $"Git fetch failed (exit code {exitCode})";
            logger.LogError("Git fetch failed in {ElapsedMs}ms for {RepoPath}. Args={Args}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, logArgs, exitCode, stdout, stderr);
            return (false, combinedOutput);
        }
        logger.LogDebug("Git fetch completed in {ElapsedMs}ms for {RepoPath}. Args={Args}", sw.ElapsedMilliseconds, repoPath, logArgs);
        return (true, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> FetchMinimalAsync(string repoPath, string branchName, string? defaultBranchOriginRef, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (true, null);

        logger.LogDebug("Git minimal fetch starting for {RepoPath}. InputBranch={Branch}, DefaultBranchOriginRef={DefaultRef}, HasBearer={HasBearer}",
            repoPath,
            branchName,
            defaultBranchOriginRef,
            !string.IsNullOrWhiteSpace(bearerToken));

        var refsToFetch = new List<string>();

        // Resolve the upstream ref (e.g. origin/main) and use that as the fetch target for the
        // current branch rather than the branch name itself. This avoids fetching non-upstreamed
        // local branches and ensures we are always fetching the configured remote tracking ref.
        var upstreamRef = await GetUpstreamRefAsync(repoPath, ct);
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

        var refArgs = string.Join(" ", refsToFetch);

        string args;
        var logArgs = "";
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            args = $"fetch origin {refArgs}";
            logArgs = args;
        }
        else
        {
            var credentials = "x-access-token:" + bearerToken;
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            var headerValue = "Authorization: Basic " + base64;
            var escaped = headerValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
            args = $"-c core.askpass=true -c credential.helper= -c \"http.extraHeader={escaped}\" fetch origin {refArgs}";
            logArgs = "***";
        }

        logger.LogDebug("Git minimal fetch invoking git for {RepoPath}. Args={Args}, Refs={Refs}", repoPath, logArgs, string.Join(", ", refsToFetch));

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await MinimalFetchRetryPipeline.ExecuteAsync(
            async cancellationToken => await RunProcessAsync("git", args, repoPath, cancellationToken),
            ct);
        sw.Stop();
        logger.LogDebug("Git minimal fetch git process completed for {RepoPath} in {ElapsedMs}ms. ExitCode={ExitCode}", repoPath, sw.ElapsedMilliseconds, exitCode);
        if (exitCode != 0)
        {
            var output = (stdout ?? "").Trim();
            var errorOutput = (stderr ?? "").Trim();
            var combinedOutput = string.IsNullOrWhiteSpace(output) ? errorOutput :
                                 string.IsNullOrWhiteSpace(errorOutput) ? output :
                                 $"{output}\n{errorOutput}";
            if (string.IsNullOrWhiteSpace(combinedOutput))
                combinedOutput = $"Git fetch (minimal) failed (exit code {exitCode})";
            logger.LogError("Git minimal fetch failed in {ElapsedMs}ms for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, exitCode, stdout, stderr);
            return (false, combinedOutput);
        }

        logger.LogDebug("Git minimal fetch completed in {ElapsedMs}ms for {RepoPath}. Refs={Refs}", sw.ElapsedMilliseconds, repoPath, string.Join(", ", refsToFetch));
        return (true, null);
    }

    public async Task<(int? Outgoing, int? Incoming, bool HasUpstream)> GetCommitCountsAsync(string repoPath, string branchName, string? defaultBranchOriginRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (null, null, false);

        var sw = Stopwatch.StartNew();

        // Resolve upstream ref (e.g. origin/main) once for this branch. When there is no upstream
        // configured we fall back to comparing against the default branch only.
        var upstreamRef = await GetUpstreamRefAsync(repoPath, ct);
        if (string.IsNullOrWhiteSpace(upstreamRef))
        {
            var defaultBranch = defaultBranchOriginRef ?? await GetDefaultBranchAsync(repoPath, ct);
            if (defaultBranch == null)
            {
                logger.LogDebug("No configured upstream for branch {Branch} and no default branch found for {RepoPath}, skipping commit counts", branchName, repoPath);
                return (null, null, false);
            }

            var (exitDefault, stdoutDefault, stderrDefault) = await RunProcessAsync("git", $"rev-list --count {defaultBranch}..HEAD", repoPath, ct);
            if (exitDefault != 0)
            {
                logger.LogWarning("Git rev-list (outgoing vs default branch) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitDefault, stdoutDefault, stderrDefault);
                return (null, null, false);
            }

            var aheadCount = int.TryParse((stdoutDefault ?? "").Trim(), out var ahead) ? ahead : (int?)null;
            sw.Stop();
            logger.LogDebug("GetCommitCounts (vs default branch, no upstream) completed in {ElapsedMs}ms for {RepoPath}", sw.ElapsedMilliseconds, repoPath);
            return (aheadCount, null, false);
        }

        var originBranch = upstreamRef!;

        // Branch has an upstream configured - verify the remote-tracking ref exists locally before comparing.
        var (exitCheck, _, _) = await RunProcessAsync("git", $"rev-parse --verify {originBranch}", repoPath, ct);
        if (exitCheck != 0)
        {
            var defaultBranch = defaultBranchOriginRef ?? await GetDefaultBranchAsync(repoPath, ct);
            if (defaultBranch == null)
            {
                logger.LogDebug("Configured upstream for {Branch}, but remote {OriginBranch} not found and no default branch for {RepoPath}, skipping commit counts", branchName, originBranch, repoPath);
                return (null, null, false);
            }

            var (exitDefault, stdoutDefault, stderrDefault) = await RunProcessAsync("git", $"rev-list --count {defaultBranch}..HEAD", repoPath, ct);
            if (exitDefault != 0)
            {
                logger.LogWarning("Git rev-list (outgoing vs default branch) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitDefault, stdoutDefault, stderrDefault);
                return (null, null, false);
            }

            var aheadCount = int.TryParse((stdoutDefault ?? "").Trim(), out var ahead) ? ahead : (int?)null;
            sw.Stop();
            logger.LogDebug("GetCommitCounts (vs default branch, missing remote upstream) completed in {ElapsedMs}ms for {RepoPath}", sw.ElapsedMilliseconds, repoPath);
            return (aheadCount, null, false);
        }

        // Branch exists upstream - use standard comparison
        var (exitOut, stdoutOut, stderrOut) = await RunProcessAsync("git", $"rev-list --count {originBranch}..HEAD", repoPath, ct);
        var (exitIn, stdoutIn, stderrIn) = await RunProcessAsync("git", $"rev-list --count HEAD..{originBranch}", repoPath, ct);

        if (exitOut != 0)
        {
            logger.LogWarning("Git rev-list (outgoing) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitOut, stdoutOut, stderrOut);
            return (null, null, true);
        }
        if (exitIn != 0)
        {
            logger.LogWarning("Git rev-list (incoming) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitIn, stdoutIn, stderrIn);
            return (null, null, true);
        }

        var outVal = int.TryParse((stdoutOut ?? "").Trim(), out var o) ? o : (int?)null;
        var inVal = int.TryParse((stdoutIn ?? "").Trim(), out var i) ? i : (int?)null;
        sw.Stop();
        logger.LogDebug("GetCommitCounts completed in {ElapsedMs}ms for {RepoPath} (↑{Outgoing} ↓{Incoming})", sw.ElapsedMilliseconds, repoPath, outVal, inVal);
        return (outVal, inVal, true);
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

        // ahead = commits on current branch not in default; behind = commits on default not in current branch. Run both in parallel.
        var aheadTask = RunProcessAsync("git", $"rev-list --count {defaultBranch}..HEAD", repoPath, ct);
        var behindTask = RunProcessAsync("git", $"rev-list --count HEAD..{defaultBranch}", repoPath, ct);
        await Task.WhenAll(aheadTask, behindTask);
        var (exitAhead, stdoutAhead, stderrAhead) = await aheadTask;
        var (exitBehind, stdoutBehind, stderrBehind) = await behindTask;

        if (exitAhead != 0)
        {
            logger.LogDebug("GetCommitCountsVsDefault (ahead) failed for {RepoPath}. ExitCode={ExitCode}", repoPath, exitAhead);
            return (null, null, null);
        }
        if (exitBehind != 0)
        {
            logger.LogDebug("GetCommitCountsVsDefault (behind) failed for {RepoPath}. ExitCode={ExitCode}", repoPath, exitBehind);
            return (null, null, null);
        }

        var ahead = int.TryParse((stdoutAhead ?? "").Trim(), out var a) ? a : (int?)null;
        var behind = int.TryParse((stdoutBehind ?? "").Trim(), out var b) ? b : (int?)null;
        var defaultBranchName = defaultBranch.StartsWith("origin/") ? defaultBranch.Substring("origin/".Length) : defaultBranch;
        sw.Stop();
        logger.LogDebug("GetCommitCountsVsDefault completed in {ElapsedMs}ms for {RepoPath}: behind={Behind}, ahead={Ahead}", sw.ElapsedMilliseconds, repoPath, behind, ahead);
        return (behind, ahead, defaultBranchName);
    }

    public async Task<(bool Success, bool MergeConflict, string? ErrorMessage)> PullAsync(string repoPath, string branchName, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, false, "Invalid repository path or branch name");

        string args;
        var logArgs = "";
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            args = $"pull origin {branchName}";
            logArgs = args;
        }
        else
        {
            var credentials = "x-access-token:" + bearerToken;
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            var headerValue = "Authorization: Basic " + base64;
            var escaped = headerValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
            args = $"-c core.askpass=true -c credential.helper= -c \"http.extraHeader={escaped}\" pull origin {branchName}";
            logArgs = "***";
        }

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await PullRetryPipeline.ExecuteAsync(
            async cancellationToken => await RunProcessAsync("git", args, repoPath, cancellationToken),
            ct);
        sw.Stop();

        if (exitCode != 0)
        {
            // Combine stdout and stderr for error message
            var output = (stdout ?? "").Trim();
            var errorOutput = (stderr ?? "").Trim();
            var combinedOutput = string.IsNullOrWhiteSpace(output) ? errorOutput :
                                 string.IsNullOrWhiteSpace(errorOutput) ? output :
                                 $"{output}\n{errorOutput}";

            // Check if it's a merge conflict
            var isMergeConflict = combinedOutput.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase) ||
                                  combinedOutput.Contains("merge conflict", StringComparison.OrdinalIgnoreCase) ||
                                  combinedOutput.Contains("Automatic merge failed", StringComparison.OrdinalIgnoreCase);

            if (isMergeConflict)
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
            var credentials = "x-access-token:" + bearerToken;
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            var headerValue = "Authorization: Basic " + base64;
            var escaped = headerValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
            args = $"-c core.askpass=true -c credential.helper= -c \"http.extraHeader={escaped}\" push {pushOpts}origin {branchName}";
            logArgs = "***";
        }

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await PushRetryPipeline.ExecuteAsync(
            async cancellationToken => await RunProcessAsync("git", args, repoPath, cancellationToken),
            ct);
        sw.Stop();
        if (exitCode != 0)
        {
            // Combine stdout and stderr for error message
            var output = (stdout ?? "").Trim();
            var errorOutput = (stderr ?? "").Trim();
            var combinedOutput = string.IsNullOrWhiteSpace(output) ? errorOutput :
                                 string.IsNullOrWhiteSpace(errorOutput) ? output :
                                 $"{output}\n{errorOutput}";

            logger.LogError("Git push failed in {ElapsedMs}ms for {RepoPath}. Args={Args}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, logArgs, exitCode, stdout, stderr);
            return (false, combinedOutput);
        }

        logger.LogInformation("Git push completed in {ElapsedMs}ms for {RepoPath}. Args={Args}, Branch={Branch}", sw.ElapsedMilliseconds, repoPath, logArgs, branchName);
        return (true, null);
    }

    public async Task AbortMergeAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return;

        var (exitCode, stdout, stderr) = await RunProcessAsync("git", "merge --abort", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogWarning("Git merge --abort failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
        }
        else
        {
            logger.LogInformation("Git merge aborted for {RepoPath}", repoPath);
        }
    }

    public async Task<IReadOnlyList<string>> GetLocalBranchesAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return Array.Empty<string>();

        var (exitCode, stdout, stderr) = await RunProcessAsync("git", "branch --format=%(refname:short)", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogWarning("Git branch list failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return Array.Empty<string>();
        }

        var branches = (stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .OrderBy(b => b)
            .ToList();

        return branches;
    }

    public async Task<IReadOnlyList<string>> GetRemoteBranchesFromRefsAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return Array.Empty<string>();

        // Local refs only (no network). Use after fetch when refs/remotes/origin is up to date.
        var (exitCode, stdout, stderr) = await RunProcessAsync("git", "for-each-ref refs/remotes/origin --format=%(refname:short)", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogDebug("Git for-each-ref refs/remotes/origin failed for {RepoPath}. ExitCode={ExitCode}", repoPath, exitCode);
            return Array.Empty<string>();
        }

        const string originPrefix = "origin/";
        var branches = (stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => !string.IsNullOrWhiteSpace(b) && b.StartsWith(originPrefix, StringComparison.Ordinal))
            .Select(b => b.Substring(originPrefix.Length))
            .Where(b => !string.IsNullOrWhiteSpace(b) && b != "HEAD")
            .OrderBy(b => b)
            .ToList();

        return branches;
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
            var credentials = "x-access-token:" + bearerToken;
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            var headerValue = "Authorization: Basic " + base64;
            var escaped = headerValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
            args = $"-c core.askpass=true -c credential.helper= -c \"http.extraHeader={escaped}\" ls-remote --heads origin";
            logArgs = "***";
        }

        // Use ls-remote to query the actual remote branches (not local remote-tracking refs)
        // This ensures we only see branches that actually exist on origin, not stale local references
        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await LsRemoteRetryPipeline.ExecuteAsync(
            async cancellationToken => await RunProcessAsync("git", args, repoPath, cancellationToken),
            ct);
        sw.Stop();
        if (exitCode != 0)
        {
            logger.LogWarning("Git ls-remote failed in {ElapsedMs}ms for {RepoPath}. Args={Args}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, logArgs, exitCode, stdout, stderr);
            return Array.Empty<string>();
        }

        // ls-remote output format: <commit-hash>    refs/heads/branch-name
        var branches = (stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line) && line.Contains("refs/heads/"))
            .Select(line =>
            {
                var parts = line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1].StartsWith("refs/heads/"))
                {
                    return parts[1].Substring("refs/heads/".Length);
                }
                return null;
            })
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .OrderBy(b => b!)
            .ToList();

        return branches!;
    }

    public async Task<(bool Success, string? ErrorMessage)> CheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, "Invalid repository path or branch name");

        static string CombineOutput(string? stdout, string? stderr)
        {
            var outStr = (stdout ?? "").Trim();
            var errStr = (stderr ?? "").Trim();
            return string.IsNullOrWhiteSpace(outStr) ? errStr
                 : string.IsNullOrWhiteSpace(errStr) ? outStr
                 : $"{outStr}\n{errStr}";
        }

        // First check if it's a remote branch that needs to be tracked locally
        var (exitCheckRemote, _, _) = await RunProcessAsync("git", $"rev-parse --verify origin/{branchName}", repoPath, ct);
        if (exitCheckRemote == 0)
        {
            // Remote branch exists, checkout with tracking
            var (exitCode, stdout, stderr) = await RunProcessAsync("git", $"checkout -b {branchName} origin/{branchName}", repoPath, ct);
            if (exitCode != 0)
            {
                // Try regular checkout in case branch already exists locally
                (exitCode, stdout, stderr) = await RunProcessAsync("git", $"checkout {branchName}", repoPath, ct);
            }

            if (exitCode != 0)
            {
                var combined = CombineOutput(stdout, stderr);
                logger.LogError("Git checkout failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, branchName, exitCode, stdout, stderr);
                return (false, combined);
            }
        }
        else
        {
            // Local branch only
            var (exitCode, stdout, stderr) = await RunProcessAsync("git", $"checkout {branchName}", repoPath, ct);
            if (exitCode != 0)
            {
                var combined = CombineOutput(stdout, stderr);
                logger.LogError("Git checkout failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, branchName, exitCode, stdout, stderr);
                return (false, combined);
            }
        }

        logger.LogInformation("Git checkout completed for {RepoPath}. Branch={Branch}", repoPath, branchName);
        return (true, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> CreateBranchAsync(string repoPath, string newBranchName, string baseBranchName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(newBranchName) || string.IsNullOrWhiteSpace(baseBranchName))
            return (false, "Invalid repository path or branch name");

        static string CombineOutput(string? stdout, string? stderr)
        {
            var outStr = (stdout ?? "").Trim();
            var errStr = (stderr ?? "").Trim();
            return string.IsNullOrWhiteSpace(outStr) ? errStr
                 : string.IsNullOrWhiteSpace(errStr) ? outStr
                 : $"{outStr}\n{errStr}";
        }

        // Determine the start point for the new branch without performing an extra checkout:
        // prefer origin/<baseBranchName> when it exists, otherwise fall back to the local baseBranchName.
        var trimmedBase = baseBranchName.Trim();
        var startPoint = trimmedBase;
        var remoteCandidate = trimmedBase.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
            ? trimmedBase
            : "origin/" + trimmedBase;

        var (exitRemote, _, _) = await RunProcessAsync("git", $"rev-parse --verify {remoteCandidate}", repoPath, ct);
        if (exitRemote == 0)
            startPoint = remoteCandidate;

        // Create and checkout new branch from the chosen start point in a single checkout, so we only
        // trigger one post-checkout hook instead of first checking out the base branch and then the new branch.
        var (exitCode, stdout, stderr) = await RunProcessAsync("git", $"checkout -b {newBranchName} {startPoint}", repoPath, ct);
        if (exitCode != 0)
        {
            // Branch may already exist (e.g. persistence out of date); try checkout existing
            var (verifyExit, _, _) = await RunProcessAsync("git", $"rev-parse --verify refs/heads/{newBranchName}", repoPath, ct);
            if (verifyExit == 0)
            {
                logger.LogWarning("Branch {Branch} already exists in {RepoPath}; checking out existing branch.", newBranchName, repoPath);
                var (coExit, coOut, coErr) = await RunProcessAsync("git", $"checkout {newBranchName}", repoPath, ct);
                if (coExit != 0)
                {
                    var combined = CombineOutput(coOut, coErr);
                    logger.LogError("Git checkout of existing branch failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, newBranchName, coExit);
                    return (false, combined);
                }
                return (true, null);
            }
            var combinedErr = CombineOutput(stdout, stderr);
            logger.LogError("Git create branch failed for {RepoPath}. NewBranch={NewBranch}, BaseBranch={BaseBranch}, ExitCode={ExitCode}", repoPath, newBranchName, baseBranchName, exitCode);
            return (false, combinedErr);
        }

        logger.LogInformation("Git branch created for {RepoPath}. NewBranch={NewBranch}, BaseBranch={BaseBranch}", repoPath, newBranchName, baseBranchName);
        return (true, null);
    }

    public async Task<bool> DeleteLocalBranchAsync(string repoPath, string branchName, bool force, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return false;

        // Check if branch exists
        var (exitCheck, _, _) = await RunProcessAsync("git", $"rev-parse --verify {branchName}", repoPath, ct);
        if (exitCheck != 0)
        {
            logger.LogWarning("Branch {Branch} does not exist in {RepoPath}", branchName, repoPath);
            return false;
        }

        // Check if it's the current branch
        var (exitCurrent, stdoutCurrent, _) = await RunProcessAsync("git", "branch --show-current", repoPath, ct);
        if (exitCurrent == 0 && (stdoutCurrent ?? "").Trim() == branchName)
        {
            logger.LogWarning("Cannot delete current branch {Branch} in {RepoPath}", branchName, repoPath);
            return false;
        }

        string args = force ? $"branch -D {branchName}" : $"branch -d {branchName}";
        var (exitCode, stdout, stderr) = await RunProcessAsync("git", args, repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogWarning("Git branch delete failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, branchName, exitCode, stdout, stderr);
            return false;
        }

        logger.LogInformation("Git branch deleted for {RepoPath}. Branch={Branch}", repoPath, branchName);
        return true;
    }

    public async Task<(bool Success, string? ErrorMessage)> DeleteBranchAsync(string repoPath, string branchName, bool isRemote, bool force, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, "Invalid repository path or branch name");

        static string CombineOutput(string? stdout, string? stderr)
        {
            var outStr = (stdout ?? "").Trim();
            var errStr = (stderr ?? "").Trim();
            return string.IsNullOrWhiteSpace(outStr) ? errStr
                 : string.IsNullOrWhiteSpace(errStr) ? outStr
                 : $"{outStr}\n{errStr}";
        }

        if (isRemote)
        {
            // Remote only: delete the branch on origin. Does not modify local refs (refs/heads/).
            var name = branchName.Trim();
            if (name.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
                name = name.Substring("origin/".Length);
            if (string.IsNullOrWhiteSpace(name))
                return (false, "Invalid branch name");
            var (exitCode, stdout, stderr) = await RunProcessAsync("git", $"push origin --delete {name}", repoPath, ct);
            if (exitCode != 0)
            {
                var combined = CombineOutput(stdout, stderr);
                logger.LogWarning("Git push --delete failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, name, exitCode);
                return (false, combined);
            }
            logger.LogInformation("Git remote branch deleted for {RepoPath}. Branch={Branch}", repoPath, name);
            return (true, null);
        }

        // Local only: delete the branch in refs/heads/. Does not run push or modify remote.
        var localName = branchName.Trim();
        if (localName.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            localName = localName.Substring("origin/".Length);
        if (string.IsNullOrWhiteSpace(localName))
            return (false, "Invalid branch name");

        var (exitCheck, _, _) = await RunProcessAsync("git", $"rev-parse --verify refs/heads/{localName}", repoPath, ct);
        if (exitCheck != 0)
        {
            logger.LogWarning("Branch {Branch} does not exist in {RepoPath}", localName, repoPath);
            return (false, "Branch does not exist.");
        }

        var (exitCurrent, stdoutCurrent, _) = await RunProcessAsync("git", "branch --show-current", repoPath, ct);
        if (exitCurrent == 0 && (stdoutCurrent ?? "").Trim().Equals(localName, StringComparison.Ordinal))
        {
            logger.LogWarning("Cannot delete current branch {Branch} in {RepoPath}", localName, repoPath);
            return (false, "Cannot delete the current branch. Check out another branch first.");
        }

        // Force delete: git branch -D (user confirmed after -d reported not fully merged)
        if (force)
        {
            var (exitForce, stdoutForce, stderrForce) = await RunProcessAsync("git", $"branch -D {localName}", repoPath, ct);
            if (exitForce != 0)
            {
                var combinedForce = CombineOutput(stdoutForce, stderrForce);
                logger.LogWarning("Git branch force delete failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, localName, exitForce);
                return (false, combinedForce);
            }
            logger.LogInformation("Git branch force deleted for {RepoPath}. Branch={Branch}", repoPath, localName);
            return (true, null);
        }

        var args = $"branch -d {localName}";
        var (exitCodeLocal, stdoutLocal, stderrLocal) = await RunProcessAsync("git", args, repoPath, ct);
        if (exitCodeLocal != 0)
        {
            var combined = CombineOutput(stdoutLocal, stderrLocal);
            logger.LogWarning("Git branch delete failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, localName, exitCodeLocal);
            return (false, combined);
        }

        logger.LogInformation("Git branch deleted for {RepoPath}. Branch={Branch}", repoPath, localName);
        return (true, null);
    }

    public async Task<string?> GetDefaultBranchNameAsync(string repoPath, CancellationToken ct)
    {
        var defaultBranch = await GetDefaultBranchAsync(repoPath, ct);
        if (defaultBranch == null)
            return null;

        // Remove "origin/" prefix
        if (defaultBranch.StartsWith("origin/"))
            return defaultBranch.Substring("origin/".Length);

        return defaultBranch;
    }

    public Task<string?> GetDefaultBranchOriginRefAsync(string repoPath, CancellationToken ct)
        => GetDefaultBranchAsync(repoPath, ct);

    /// <summary>
    /// Finds the default branch on origin (e.g. origin/main). Tries symbolic-ref first, then origin/main, then origin/master.
    /// </summary>
    private async Task<string?> GetDefaultBranchAsync(string repoPath, CancellationToken ct)
    {
        // Try configured default first (single git call in common case)
        var (exitHead, stdoutHead, _) = await RunProcessAsync("git", "symbolic-ref refs/remotes/origin/HEAD", repoPath, ct);
        if (exitHead == 0 && !string.IsNullOrWhiteSpace(stdoutHead))
        {
            var refName = stdoutHead.Trim();
            if (refName.StartsWith("refs/remotes/origin/"))
            {
                var branch = refName.Substring("refs/remotes/origin/".Length);
                if (!string.IsNullOrEmpty(branch) && branch != "HEAD")
                {
                    var (exitVerify, _, _) = await RunProcessAsync("git", $"rev-parse --verify origin/{branch}", repoPath, ct);
                    if (exitVerify == 0)
                        return $"origin/{branch}";
                }
            }
        }

        // Fall back to origin/main then origin/master
        var (exitMain, _, _) = await RunProcessAsync("git", "rev-parse --verify origin/main", repoPath, ct);
        if (exitMain == 0)
            return "origin/main";

        var (exitMaster, _, _) = await RunProcessAsync("git", "rev-parse --verify origin/master", repoPath, ct);
        if (exitMaster == 0)
            return "origin/master";

        return null;
    }

    /// <summary>
    /// Returns the configured upstream ref for the current HEAD (e.g. "origin/main") by using
    /// `git rev-parse --abbrev-ref --symbolic-full-name @{u}`. Returns null when no upstream is configured.
    /// </summary>
    private async Task<string?> GetUpstreamRefAsync(string repoPath, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await RunProcessAsync(
            "git",
            "rev-parse --abbrev-ref --symbolic-full-name @{u}",
            repoPath,
            ct);

        if (exitCode != 0)
            return null;

        var name = (stdout ?? "").Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public async Task<(bool Success, string? ErrorMessage)> StageAndCommitAsync(string repoPath, IReadOnlyList<string> pathsToStage, string commitMessage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (false, "Invalid repository path");
        if (pathsToStage == null || pathsToStage.Count == 0)
            return (false, "No paths to stage");
        if (string.IsNullOrWhiteSpace(commitMessage))
            return (false, "Commit message is required");

        var paths = pathsToStage.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).Distinct().ToList();
        if (paths.Count == 0)
            return (false, "No paths to stage");

        var addArgs = "add -- " + string.Join(" ", paths.Select(p => p.Contains(' ') ? "\"" + p.Replace("\"", "\\\"") + "\"" : p));
        var (addExit, addOut, addErr) = await RunProcessAsync("git", addArgs, repoPath, ct);
        if (addExit != 0)
        {
            var err = (addErr ?? addOut ?? "").Trim();
            logger.LogError("Git add failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}", repoPath, addExit, err);
            return (false, err);
        }

        // Skip commit when staging resulted in no index changes.
        var (stagedExit, _, stagedErr) = await RunProcessAsync("git", "diff --cached --quiet", repoPath, ct);
        if (stagedExit == 0)
        {
            logger.LogInformation("Git stage and commit skipped for {RepoPath}: nothing staged to commit", repoPath);
            return (true, null);
        }
        if (stagedExit != 1)
        {
            var err = (stagedErr ?? "").Trim();
            logger.LogError("Git staged diff check failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}", repoPath, stagedExit, err);
            return (false, string.IsNullOrWhiteSpace(err) ? "Failed to verify staged changes before commit." : err);
        }

        var messageNormalized = commitMessage.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
        var (commitExit, commitOut, commitErr) = await RunProcessWithStdinAsync("git", "commit -F -", repoPath, messageNormalized, ct);
        if (commitExit != 0)
        {
            var err = (commitErr ?? commitOut ?? "").Trim();
            logger.LogError("Git commit failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}", repoPath, commitExit, err);
            return (false, err);
        }

        logger.LogInformation("Git stage and commit completed for {RepoPath}", repoPath);
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

    public void WriteSyncHooks(string repoPath, int workspaceId, int repositoryId)
    {
        var hooksDir = Path.Combine(repoPath, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);

        var jsonPayload = JsonSerializer.Serialize(new { repositoryId, workspaceId, repositoryPath = repoPath });
        var escapedPayload = jsonPayload.Replace("'", "'\\'' ");
        var header = "-H \"Content-Type: application/json\"";
        var curlFlags = "-s --connect-timeout 1 --max-time 2";
        var commitCurl = $"curl {curlFlags} -X POST \"http://127.0.0.1:{_listenPort}/hook/commit\"   {header} -d '{escapedPayload}' || true";
        var checkoutCurl = $"curl {curlFlags} -X POST \"http://127.0.0.1:{_listenPort}/hook/checkout\" {header} -d '{escapedPayload}' || true";
        var mergeCurl = $"curl {curlFlags} -X POST \"http://127.0.0.1:{_listenPort}/hook/merge\"    {header} -d '{escapedPayload}' || true";
        var pushCurl = $"curl {curlFlags} -X POST \"http://127.0.0.1:{_listenPort}/hook/push\"     {header} -d '{escapedPayload}' || true";

        var utf8 = new UTF8Encoding(false);
        var comment = $"# Created by GrayMoon.Agent at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n";

        WriteHookFile(Path.Combine(hooksDir, "post-commit"), "#!/bin/sh\n" + comment + commitCurl + "\n", utf8);
        WriteHookFile(Path.Combine(hooksDir, "post-checkout"), "#!/bin/sh\n" + comment + "[ \"$3\" = \"1\" ] && " + checkoutCurl.TrimEnd() + "\n", utf8);
        WriteHookFile(Path.Combine(hooksDir, "post-merge"), "#!/bin/sh\n" + comment + mergeCurl + "\n", utf8);
        WriteHookFile(Path.Combine(hooksDir, "post-update"), "#!/bin/sh\n" + comment + commitCurl + "\n", utf8);
        WriteHookFile(Path.Combine(hooksDir, "pre-push"), "#!/bin/sh\n" + comment + pushCurl + "\n", utf8);
        logger.LogDebug("Sync hooks written for repo {RepoId} in workspace {WorkspaceId}", repositoryId, workspaceId);
    }

    private static void WriteHookFile(string path, string content, Encoding encoding)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        File.WriteAllText(path, normalized, encoding);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string BuildCloneArguments(string cloneUrl, string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            return $"clone \"{cloneUrl}\"";
        var credentials = "x-access-token:" + bearerToken;
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        var headerValue = "Authorization: Basic " + base64;
        var escaped = headerValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"-c core.askpass=true -c credential.helper= -c \"http.extraHeader={escaped}\" clone \"{cloneUrl}\"";
    }

    private static string SanitizeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "workspace";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Trim().Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "workspace" : sanitized;
    }

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> RunProcessAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        CancellationToken ct)
    {
        var r = await commandLine.RunAsync(fileName, arguments, workingDirectory, null, ct);
        return (r.ExitCode, r.Stdout, r.Stderr);
    }

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> RunProcessWithStdinAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        string stdinContent,
        CancellationToken ct)
    {
        var r = await commandLine.RunAsync(fileName, arguments, workingDirectory, stdinContent, ct);
        return (r.ExitCode, r.Stdout, r.Stderr);
    }
}
