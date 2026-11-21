using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services.GitChanges;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static GrayMoon.Worker.Services.GitCliOutput;

namespace GrayMoon.Worker.Services;

public sealed class GitService(IOptions<WorkerOptions> options, ILogger<GitService> logger, GitProcessRunner runner, IGitRepositoryReader reader, IGitIgnoreService ignore, IRepositoryAccess? access = null) : IGitService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _safeRepoCache = new(StringComparer.OrdinalIgnoreCase);
    private static string? _emptyHooksPath;

    // The clone's own checkout must already allow paths over 260 characters on Windows, before any repository config
    // exists to say so. Command-scoped (-c): nothing is written to global or system config; the persistent local
    // setting is written afterwards by IRepositoryConfigurationInitializer.
    private static string CloneLongPathsPrefix => OperatingSystem.IsWindows() ? "-c core.longpaths=true " : "";


    public async Task<GitCloneResult> CloneAsync(string workingDir, string cloneUrl, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDir))
            throw new ArgumentException("Working directory is required.", nameof(workingDir));
        if (string.IsNullOrWhiteSpace(cloneUrl))
            throw new ArgumentException("Clone URL is required.", nameof(cloneUrl));

        if (!Directory.Exists(workingDir))
            Directory.CreateDirectory(workingDir);

        // --progress forces git to report progress on stderr even though it is not a terminal; that stream is the
        // liveness signal the runner's idle timeout watches (see GitProcessOptions.CloneIdleTimeoutSeconds).
        var args = $"{CloneLongPathsPrefix}clone --progress \"{cloneUrl}\"";

        var cloneFolderName = CloneFolderName(cloneUrl);
        var cloneTarget = cloneFolderName == null ? null : Path.Combine(workingDir, cloneFolderName);
        if (cloneTarget != null && Directory.Exists(cloneTarget) && Directory.EnumerateFileSystemEntries(cloneTarget).Any())
        {
            logger.LogError("Git clone of {Url} refused: destination {Dir} already exists and is not empty.", cloneUrl, cloneTarget);
            return GitCloneResult.FromFailure(new GitRemoteFailure(128, GitRemoteFailureKind.Unknown,
                $"Cannot clone: the folder '{cloneFolderName}' already exists in '{workingDir}' and is not empty.", null, null, null));
        }

        // A killed or failed attempt leaves a half-written folder behind. Remove it before each attempt (and after the
        // last), otherwise the retry fails with git's misleading "destination path already exists" and hides the real error.
        void DiscardPartialClone()
        {
            if (cloneTarget != null)
                DeletePartialClone(cloneTarget, deleteRoot: true);
        }

        var sw = Stopwatch.StartNew();
        var run = await runner.RunRemoteAsync(GitRemoteOperation.Clone, args, workingDir, bearerToken, ct, DiscardPartialClone);
        sw.Stop();
        if (run.ExitCode != 0)
        {
            DiscardPartialClone();
            var failure = GitHubSsoAuthorization.Describe(run.ExitCode, run.Stdout, run.Stderr, run.AuthorizationUrl);
            LogIfAuthFailure("clone", workingDir, run.Stdout, run.Stderr);
            logger.LogError("Git clone failed after retries in {ElapsedMs}ms. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, run.ExitCode, run.Stdout, run.Stderr);
            return GitCloneResult.FromFailure(failure);
        }
        logger.LogInformation("Git clone completed in {ElapsedMs}ms: {Url} -> {Dir}", sw.ElapsedMilliseconds, cloneUrl, workingDir);
        return GitCloneResult.Succeeded(run.ExitCode);
    }

    /// <summary>Folder name <c>git clone &lt;url&gt;</c> creates by default (last path segment without <c>.git</c>); null when unknown.</summary>
    internal static string? CloneFolderName(string cloneUrl)
    {
        var s = cloneUrl.Trim().TrimEnd('/', '\\');
        if (s.EndsWith("/.git", StringComparison.OrdinalIgnoreCase))
            s = s[..^5];
        var i = s.LastIndexOfAny(['/', '\\', ':']);
        var name = i >= 0 ? s[(i + 1)..] : s;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : name;
    }

    private void DeletePartialClone(string path, bool deleteRoot)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best-effort */ }
            }

            if (deleteRoot)
            {
                Directory.Delete(path, recursive: true);
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove partial clone at {Path}.", path);
        }
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

        var (isSafe, isDubiousOwnership) = await CheckRepoSafeAsync(repoPath, pathForGit, ct);
        logger.LogDebug("Git repo safety check: {Path} -> {Result}", pathForGit, isSafe ? "safe" : "not safe");

        if (isSafe)
        {
            _safeRepoCache.TryAdd(fullPath, 0);
            logger.LogDebug("Repository already safe, skipping safe.directory update: {Path}", pathForGit);
            return;
        }

        // Trust is only extended for the one failure it fixes. Any other failure (not a repository, corrupt, git
        // missing, ...) is left for the operation that follows to report, and is not cached so it is probed again.
        if (!isDubiousOwnership)
        {
            logger.LogWarning("Git could not use {Path}, but not because of dubious ownership; safe.directory was not changed.", pathForGit);
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
                    var retryError = GitVersionFailureText.Summarize(retryStderr, retryStdout)
                        ?? BuildProcessError(retryStderr, retryStdout, $"{toolName} exited with code {retryExitCode}");
                    logger.LogError("{ToolName} failed after tool restore. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", toolName, retryExitCode, retryStdout, retryStderr);
                    return (null, retryError);
                }
                stdout = retryStdout;
            }
            else
            {
                // A repository without commits has no version. That is its normal state, not a tool failure, so the
                // row gets one sentence and the log one line instead of GitVersion's stack trace.
                if (GitVersionFailureText.IsNoCommits(stderr, stdout))
                {
                    logger.LogInformation("{ToolName} found no commits on the current branch of {RepoPath}; the version stays unresolved.", toolName, repoPath);
                    return (null, GitVersionFailureText.NoCommits);
                }

                var error = GitVersionFailureText.Summarize(stderr, stdout)
                    ?? BuildProcessError(stderr, stdout, $"{toolName} exited with code {exitCode}");
                if (manifestExists)
                    logger.LogWarning("{ToolName} failed in {ElapsedMs}ms in {RepoPath}. Tool manifest exists, but failure does not look like a missing local tool; skipping 'dotnet tool restore'. ExitCode={ExitCode}", toolName, sw.ElapsedMilliseconds, repoPath, exitCode);
                // A warning, not an error: the repository keeps syncing with an unresolved version.
                logger.LogWarning("{ToolName} failed in {ElapsedMs}ms. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", toolName, sw.ElapsedMilliseconds, exitCode, stdout, stderr);
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







    public async Task<(bool Success, string? ErrorMessage)> FetchAsync(string repoPath, bool includeTags, string? bearerToken, CancellationToken ct, bool skipHooks = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (true, null);

        var args = GetHooksConfigPrefix(skipHooks) + (includeTags ? "fetch origin --prune --tags" : "fetch origin --prune");
        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.RunRemoteAsync(GitRemoteOperation.Fetch, args, repoPath, bearerToken, ct);
        sw.Stop();
        if (exitCode != 0)
        {
            var combined = GitRemoteAuth.WithAuthHint(CombineOutput(stdout, stderr)) ?? $"Git fetch failed (exit code {exitCode})";
            logger.LogError("Git fetch failed in {ElapsedMs}ms for {RepoPath}. Args={Args}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, args, exitCode, stdout, stderr);
            return (false, combined);
        }
        logger.LogDebug("Git fetch completed in {ElapsedMs}ms for {RepoPath}. Args={Args}", sw.ElapsedMilliseconds, repoPath, args);
        return (true, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> FetchMinimalAsync(string repoPath, string branchName, string? defaultBranchOriginRef, string? bearerToken, CancellationToken ct, bool skipUpstreamCheck = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (true, null);

        logger.LogDebug("Git minimal fetch starting for {RepoPath}. InputBranch={Branch}, DefaultBranchOriginRef={DefaultRef}, HasBearer={HasBearer}",
            repoPath, branchName, defaultBranchOriginRef, !string.IsNullOrWhiteSpace(bearerToken));

        var refsToFetch = new List<string>();

        var upstreamRef = skipUpstreamCheck ? null : await reader.GetUpstreamRefAsync(repoPath, branchName, ct);
        logger.LogDebug("Git minimal fetch upstream ref for {RepoPath}: {UpstreamRef}", repoPath, upstreamRef ?? "<none>");

        if (!string.IsNullOrWhiteSpace(upstreamRef))
        {
            var upstream = upstreamRef!;
            if (upstream.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
                upstream = upstream.Substring("origin/".Length);
            if (!string.IsNullOrWhiteSpace(upstream))
                refsToFetch.Add(upstream);
        }

        var defaultRef = defaultBranchOriginRef ?? await reader.GetDefaultBranchOriginRefAsync(repoPath, ct);
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
            var combined = GitRemoteAuth.WithAuthHint(CombineOutput(stdout, stderr)) ?? $"Git fetch (minimal) failed (exit code {exitCode})";
            logger.LogError("Git minimal fetch failed in {ElapsedMs}ms for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, exitCode, stdout, stderr);
            return (false, combined);
        }

        logger.LogDebug("Git minimal fetch completed in {ElapsedMs}ms for {RepoPath}. Refs={Refs}", sw.ElapsedMilliseconds, repoPath, string.Join(", ", refsToFetch));
        return (true, null);
    }

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> RunMinimalFetchAsync(string repoPath, IReadOnlyList<string> refsToFetch, string? bearerToken, CancellationToken ct)
    {
        var refArgs = string.Join(" ", refsToFetch);
        var args = $"fetch origin --prune {refArgs}";

        logger.LogDebug("Git minimal fetch invoking git for {RepoPath}. Args={Args}, Refs={Refs}",
            repoPath, args, string.Join(", ", refsToFetch));

        return await runner.RunRemoteAsync(GitRemoteOperation.MinimalFetch, args, repoPath, bearerToken, ct);
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




    public async Task<(bool Success, bool MergeConflict, string? ErrorMessage)> PullAsync(string repoPath, string branchName, string? bearerToken, CancellationToken ct, bool skipHooks = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, false, "Invalid repository path or branch name");

        var hooksPrefix = GetHooksConfigPrefix(skipHooks);

        var args = $"{hooksPrefix}pull origin {branchName}";
        var logArgs = args;

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.RunRemoteAsync(GitRemoteOperation.Pull, args, repoPath, bearerToken, ct);
        sw.Stop();

        if (exitCode != 0)
        {
            var combinedOutput = GitRemoteAuth.WithAuthHint(CombineOutput(stdout, stderr)) ?? "";
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

    public async Task<(bool Success, bool LocalChangesBlock, string? ErrorMessage)> PullFastForwardOnlyAsync(string repoPath, string branchName, string? bearerToken, CancellationToken ct, bool skipHooks = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName) || branchName.StartsWith('-'))
            return (false, false, "Invalid repository path or branch name");

        var args = $"{GetHooksConfigPrefix(skipHooks)}pull --ff-only origin {branchName}";

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.RunRemoteAsync(GitRemoteOperation.Pull, args, repoPath, bearerToken, ct);
        sw.Stop();

        if (exitCode != 0)
        {
            var combinedOutput = GitRemoteAuth.WithAuthHint(CombineOutput(stdout, stderr)) ?? "";
            var localChangesBlock = IsBlockedByLocalChanges(stdout, stderr);
            logger.LogWarning("Git fast-forward pull refused in {ElapsedMs}ms for {RepoPath}. Args={Args}, ExitCode={ExitCode}, LocalChangesBlock={LocalChangesBlock}, Stdout={Stdout}, Stderr={Stderr}", sw.ElapsedMilliseconds, repoPath, args, exitCode, localChangesBlock, stdout, stderr);
            return (false, localChangesBlock, combinedOutput);
        }

        logger.LogInformation("Git fast-forward pull completed in {ElapsedMs}ms for {RepoPath}. Args={Args}, Branch={Branch}", sw.ElapsedMilliseconds, repoPath, args, branchName);
        return (true, false, null);
    }

    /// <summary>Git's refusal to update the working tree because tracked or untracked local files are in the way.</summary>
    internal static bool IsBlockedByLocalChanges(string? stdout, string? stderr)
    {
        var combined = string.Concat(stdout ?? string.Empty, stderr ?? string.Empty);
        return combined.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("commit your changes or stash them", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<(bool Success, string? ErrorMessage)> PushAsync(string repoPath, string branchName, string? bearerToken, bool setTracking = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, "Invalid repository path or branch name");

        var pushOpts = setTracking ? "-u " : "";
        var args = $"push {pushOpts}origin {branchName}";
        var logArgs = args;

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.RunRemoteAsync(GitRemoteOperation.Push, args, repoPath, bearerToken, ct);
        sw.Stop();
        if (exitCode != 0)
        {
            var combined = GitRemoteAuth.WithAuthHint(CombineOutput(stdout, stderr)) ?? "";
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




    public async Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return Array.Empty<string>();

        const string args = "ls-remote --heads origin";
        var logArgs = args;

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.RunRemoteAsync(GitRemoteOperation.LsRemote, args, repoPath, bearerToken, ct);
        sw.Stop();
        if (exitCode != 0)
        {
            LogIfAuthFailure("ls-remote", repoPath, stdout, stderr);
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

        if (await reader.RefExistsAsync(repoPath, $"refs/heads/{branchName}", ct))
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
            if (await reader.RefExistsAsync(repoPath, $"origin/{branchName}", ct))
            {
                var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"{hooksPrefix}checkout -b {branchName} origin/{branchName}", repoPath, ct);
                if (exitCode != 0)
                {
                    logger.LogError("Git checkout failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, branchName, exitCode, stdout, stderr);
                    return (false, CombineOutput(stdout, stderr));
                }
            }
            else if (!await reader.RefExistsAsync(repoPath, "HEAD", ct))
            {
                // A repository without commits has no branch to check out: a branch there is only the name the
                // unborn HEAD points at ("pathspec did not match"). The name it is already on is a success, any
                // other name moves the unborn HEAD, which keeps the index and the files.
                if (!string.Equals(await reader.GetCurrentBranchNameAsync(repoPath, ct), branchName, StringComparison.Ordinal))
                {
                    var (exitCode, stdout, stderr) = await runner.RunAsync("git", [.. GetHooksConfigArgs(skipHooks), "checkout", "-b", branchName, "--"], repoPath, null, ct);
                    if (exitCode != 0)
                    {
                        logger.LogError("Git checkout failed for {RepoPath}, which has no commits. Branch={Branch}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, branchName, exitCode, stdout, stderr);
                        return (false, CombineOutput(stdout, stderr));
                    }
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

    public async Task<(bool Success, string? ErrorMessage, bool AlreadyExisted)> CreateBranchAsync(string repoPath, string newBranchName, string baseBranchName, CancellationToken ct, bool skipHooks = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(newBranchName) || string.IsNullOrWhiteSpace(baseBranchName))
            return (false, "Invalid repository path or branch name", false);

        var hooksArgs = GetHooksConfigArgs(skipHooks);

        // A branch of that name is already there: it is checked out as it is, the requested base is not used, and
        // the caller is told through AlreadyExisted so it can say so. Decided before the create, so a create that
        // fails for any other reason (a bad base, local changes in the way) is a failure and never ends on another branch.
        if (await reader.RefExistsAsync(repoPath, $"refs/heads/{newBranchName}", ct))
        {
            logger.LogWarning("Branch {Branch} already exists in {RepoPath}; checking out the existing branch, base {BaseBranch} is not used.", newBranchName, repoPath, baseBranchName);
            var (coExit, coOut, coErr) = await runner.RunAsync("git", [.. hooksArgs, "checkout", newBranchName, "--"], repoPath, null, ct);
            if (coExit != 0)
            {
                logger.LogError("Git checkout of existing branch failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, newBranchName, coExit);
                return (false, CombineOutput(coOut, coErr), true);
            }
            return (true, null, true);
        }

        var trimmedBase = baseBranchName.Trim();
        var startPoint = trimmedBase;
        var remoteCandidate = trimmedBase.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
            ? trimmedBase
            : "origin/" + trimmedBase;

        if (await reader.RefExistsAsync(repoPath, remoteCandidate, ct))
            startPoint = remoteCandidate;

        // A repository without commits has no base to branch from ("'main' is not a commit"). Its current branch is
        // only a name, so the new branch takes its place: git moves the unborn HEAD and keeps the index and the files.
        // Only when the base is missing too; a base that exists is used as in any other repository.
        if (!await reader.RefExistsAsync(repoPath, "HEAD", ct) && !await reader.RefExistsAsync(repoPath, startPoint, ct))
        {
            var (unbornExit, unbornOut, unbornErr) = await runner.RunAsync("git", [.. hooksArgs, "checkout", "-b", newBranchName, "--"], repoPath, null, ct);
            if (unbornExit != 0)
            {
                logger.LogError("Git create branch failed for {RepoPath}, which has no commits. NewBranch={NewBranch}, ExitCode={ExitCode}", repoPath, newBranchName, unbornExit);
                return (false, CombineOutput(unbornOut, unbornErr), false);
            }

            logger.LogInformation("Git branch {NewBranch} set as the current branch of {RepoPath}, which has no commits; base {BaseBranch} is not used.", newBranchName, repoPath, baseBranchName);
            return (true, null, false);
        }

        var (exitCode, stdout, stderr) = await runner.RunAsync("git", [.. hooksArgs, "checkout", "-b", newBranchName, "--no-track", startPoint, "--"], repoPath, null, ct);
        if (exitCode != 0)
        {
            logger.LogError("Git create branch failed for {RepoPath}. NewBranch={NewBranch}, BaseBranch={BaseBranch}, ExitCode={ExitCode}", repoPath, newBranchName, baseBranchName, exitCode);
            return (false, CombineOutput(stdout, stderr), false);
        }

        logger.LogInformation("Git branch created for {RepoPath}. NewBranch={NewBranch}, BaseBranch={BaseBranch}", repoPath, newBranchName, baseBranchName);
        return (true, null, false);
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

            var defaultBranch = await reader.GetDefaultBranchNameAsync(repoPath, ct);
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
                // Ask the remote first. When the host already deleted the branch (e.g. GitHub's delete-on-merge)
                // there is nothing to fetch or push: just drop the stale tracking ref and report success, instead
                // of running a fetch and a push that are both guaranteed to fail noisily.
                if (await IsRemoteBranchAbsentAsync(repoPath, name, bearerToken, ct))
                {
                    var trackingRef = $"refs/remotes/origin/{name}";
                    if (await reader.RefExistsAsync(repoPath, trackingRef, ct))
                        await runner.RunAsync("git", $"{hooksPrefix}update-ref -d {trackingRef}", repoPath, ct);
                    logger.LogInformation("Git remote branch already absent on origin for {RepoPath}. Branch={Branch}; pruned stale tracking ref.", repoPath, name);
                    return (true, null);
                }

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
                var leaseArgs = $"{hooksPrefix}push --force-with-lease={leaseSpec} origin :{name}";
                var (leaseExit, leaseStdout, leaseStderr) = await runner.RunRemoteAsync(GitRemoteOperation.Push, leaseArgs, repoPath, bearerToken, ct);
                if (leaseExit != 0)
                {
                    var combined = GitRemoteAuth.WithAuthHint(CombineOutput(leaseStdout, leaseStderr)) ?? "";
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

            var args = $"{hooksPrefix}push origin --delete {name}";
            var (exitCode, stdout, stderr) = await runner.RunRemoteAsync(GitRemoteOperation.Push, args, repoPath, bearerToken, ct);
            if (exitCode != 0)
            {
                var combined = GitRemoteAuth.WithAuthHint(CombineOutput(stdout, stderr)) ?? "";
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

        if (!await reader.RefExistsAsync(repoPath, $"refs/heads/{localName}", ct))
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

        // The reference-transaction hook reports the branch lists to the App. A caller that updates them itself skips it.
        var localHooksPrefix = GetHooksConfigPrefix(skipHooks);

        if (force)
        {
            var (exitForce, stdoutForce, stderrForce) = await runner.RunAsync("git", $"{localHooksPrefix}branch -D {localName}", repoPath, ct);
            if (exitForce != 0)
            {
                logger.LogWarning("Git branch force delete failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, localName, exitForce);
                return (false, CombineOutput(stdoutForce, stderrForce));
            }
            logger.LogInformation("Git branch force deleted for {RepoPath}. Branch={Branch}", repoPath, localName);
            return (true, null);
        }

        // Merged means reachable from HEAD or from origin/<default>. Plain `git branch -d` would also accept the
        // branch's own (possibly stale) remote-tracking ref, which is how a branch not yet merged into the checked-out
        // branch got deleted with a "not yet merged to HEAD" warning. Decide here, then delete.
        var mergedIntoHead = await IsAncestorAsync(repoPath, $"refs/heads/{localName}", "HEAD", ct);
        if (!mergedIntoHead)
        {
            var defaultOriginRef = await reader.GetDefaultBranchOriginRefAsync(repoPath, ct);
            var mergedIntoDefault = false;
            if (!string.IsNullOrWhiteSpace(defaultOriginRef))
            {
                var defaultRef = defaultOriginRef!.StartsWith("refs/", StringComparison.Ordinal)
                    ? defaultOriginRef
                    : $"refs/remotes/{defaultOriginRef}";
                mergedIntoDefault = await IsAncestorAsync(repoPath, $"refs/heads/{localName}", defaultRef, ct);
            }

            if (!mergedIntoDefault)
            {
                logger.LogWarning("Branch {Branch} is not fully merged in {RepoPath}; not deleting.", localName, repoPath);
                return (false, $"error: The branch '{localName}' is not fully merged.");
            }

            var (exitMerged, stdoutMerged, stderrMerged) = await runner.RunAsync("git", $"{localHooksPrefix}branch -D {localName}", repoPath, ct);
            if (exitMerged != 0)
            {
                logger.LogWarning("Git branch delete failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, localName, exitMerged);
                return (false, CombineOutput(stdoutMerged, stderrMerged));
            }
            logger.LogInformation("Git branch deleted (merged into default branch) for {RepoPath}. Branch={Branch}", repoPath, localName);
            return (true, null);
        }

        var (exitCodeLocal, stdoutLocal, stderrLocal) = await runner.RunAsync("git", $"{localHooksPrefix}branch -d {localName}", repoPath, ct);
        if (exitCodeLocal != 0)
        {
            logger.LogWarning("Git branch delete failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, localName, exitCodeLocal);
            return (false, CombineOutput(stdoutLocal, stderrLocal));
        }

        logger.LogInformation("Git branch deleted for {RepoPath}. Branch={Branch}", repoPath, localName);
        return (true, null);
    }



    public async Task<(bool Success, string? ErrorMessage)> FetchTagsAsync(string repoPath, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (true, null);

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.RunRemoteAsync(GitRemoteOperation.Fetch, "fetch origin --tags", repoPath, bearerToken, ct);
        sw.Stop();
        if (exitCode != 0)
        {
            var combined = GitRemoteAuth.WithAuthHint(CombineOutput(stdout, stderr)) ?? $"Git fetch tags failed (exit code {exitCode})";
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
        if (!await reader.RefExistsAsync(repoPath, $"refs/tags/{name}", ct))
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

    public async Task<(bool Success, string? ErrorMessage)> CheckoutCommitAsync(string repoPath, string commit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || !WorkspaceDefinitionTagPin.IsFullCommitHash(commit))
            return (false, "Invalid repository path or commit hash");

        var sha = commit.Trim().ToLowerInvariant();
        if (!await reader.RefExistsAsync(repoPath, $"{sha}^{{commit}}", ct))
            return (false, $"Commit '{sha}' does not exist in the repository.");

        var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"-c advice.detachedHead=false checkout --detach {sha}", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogError("Git checkout commit failed for {RepoPath}. Commit={Commit}, ExitCode={ExitCode}", repoPath, sha, exitCode);
            return (false, CombineOutput(stdout, stderr));
        }

        var head = await reader.GetHeadCommitAsync(repoPath, ct);
        if (!string.Equals(head, sha, StringComparison.OrdinalIgnoreCase))
            return (false, $"Checkout of commit '{sha}' left HEAD at '{head ?? "(none)"}'.");

        logger.LogInformation("Git checkout commit completed for {RepoPath}. Commit={Commit}", repoPath, sha);
        return (true, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> CheckoutBranchAtCommitAsync(string repoPath, string branchName, string commit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || !WorkspaceDefinitionTagPin.IsFullCommitHash(commit))
            return (false, "Invalid repository path or commit hash");

        var branch = branchName?.Trim();
        if (!WorkspaceDefinitionRevisionPin.IsPlainBranchName(branch))
            return (false, $"'{branchName}' is not a valid branch name.");

        var sha = commit.Trim().ToLowerInvariant();
        if (!await reader.RefExistsAsync(repoPath, $"{sha}^{{commit}}", ct))
            return (false, $"Commit '{sha}' does not exist in the repository.");

        var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"checkout -B {branch} {sha}", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogError("Git checkout of a branch at a commit failed for {RepoPath}. Branch={Branch}, Commit={Commit}, ExitCode={ExitCode}", repoPath, branch, sha, exitCode);
            return (false, CombineOutput(stdout, stderr));
        }

        var head = await reader.GetHeadCommitAsync(repoPath, ct);
        if (!string.Equals(head, sha, StringComparison.OrdinalIgnoreCase))
            return (false, $"Checkout of branch '{branch}' at commit '{sha}' left HEAD at '{head ?? "(none)"}'.");

        // Without an upstream the branch could not pull. A branch the remote no longer has stays without one.
        if (await reader.RefExistsAsync(repoPath, $"refs/remotes/origin/{branch}", ct))
        {
            var (upstreamExit, upstreamOut, upstreamErr) = await runner.RunAsync("git", $"branch --set-upstream-to=origin/{branch} {branch}", repoPath, ct);
            if (upstreamExit != 0)
            {
                logger.LogError("Git set upstream failed for {RepoPath}. Branch={Branch}, ExitCode={ExitCode}", repoPath, branch, upstreamExit);
                return (false, CombineOutput(upstreamOut, upstreamErr));
            }
        }

        logger.LogInformation("Git checkout of a branch at a commit completed for {RepoPath}. Branch={Branch}, Commit={Commit}", repoPath, branch, sha);
        return (true, null);
    }




    public async Task SetDivergenceBaseBranchAsync(string repoPath, string? divergenceBaseBranch, CancellationToken ct)
    {
        var path = await GitDirectoryLocator.ResolveDivergenceBaseFilePathAsync(runner, repoPath, ct);
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















    public async Task<(bool Success, bool Committed, string? ErrorMessage)> StageAndCommitAsync(string repoPath, IReadOnlyList<string> pathsToStage, string commitMessage, CancellationToken ct, bool skipHooks = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (false, false, "Invalid repository path");
        if (pathsToStage == null || pathsToStage.Count == 0)
            return (false, false, "No paths to stage");
        if (string.IsNullOrWhiteSpace(commitMessage))
            return (false, false, "Commit message is required");

        var paths = new List<string>(pathsToStage.Count);
        foreach (var requested in pathsToStage.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var validation = GitRepositoryPathValidator.Validate(repoPath, requested);
            if (!validation.IsValid)
                return (false, false, validation.ErrorMessage);
            if (!paths.Contains(validation.NormalizedRelativePath!, StringComparer.Ordinal))
                paths.Add(validation.NormalizedRelativePath!);
        }
        if (paths.Count == 0)
            return (false, false, "No paths to stage");

        var hooksArgs = GetHooksConfigArgs(skipHooks);

        GitStageSelection selection;
        try
        {
            var classifyStart = Stopwatch.GetTimestamp();
            using var session = ignore.Open(repoPath);
            selection = session.SelectStageable(paths);
            logger.LogDebug(
                "Git ignore classification: repo={RepoPath} elapsedMs={ElapsedMs} requested={Requested} excludedUntracked={Excluded} stageable={Stageable}",
                repoPath, (long)Stopwatch.GetElapsedTime(classifyStart).TotalMilliseconds, paths.Count, selection.ExcludedUntracked.Count, selection.Stageable.Count);
        }
        catch (GitIgnoreException ex)
        {
            logger.LogError(ex, "Git ignore classification failed for {RepoPath}", repoPath);
            return (false, false, ex.Message);
        }

        if (selection.ExcludedUntracked.Count > 0)
            logger.LogInformation("Skipping {Count} gitignored path(s) in {RepoPath}", selection.ExcludedUntracked.Count, repoPath);

        if (selection.Stageable.Count > 0)
        {
            var addPrefix = hooksArgs.Concat(["--literal-pathspecs", "add"]).ToArray();
            var (addExit, addOut, addErr) = await RunPathspecOperationAsync(repoPath, addPrefix, selection.Stageable, ct);
            if (addExit != 0)
            {
                var err = (addErr ?? addOut ?? "").Trim();
                logger.LogError("Git add failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}", repoPath, addExit, err);
                return (false, false, err);
            }
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

    public async Task<(bool Success, string? ErrorMessage)> ResetToRemoteAsync(string repoPath, string branchName, bool keepChanges, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, "Invalid repository path or branch name");

        // A branch that was never pushed has no origin ref to go back to. Its outgoing commits are counted against
        // the no-upstream compare ref, so the undo goes back to where the branch left that ref. Never push here:
        // pushing first would publish the very commits being undone and leave the reset with nothing to do.
        var target = $"origin/{branchName}";
        if (!await reader.RefExistsAsync(repoPath, target, ct))
        {
            var compareRef = await reader.GetNoUpstreamCompareRefAsync(repoPath, ct);
            if (string.IsNullOrWhiteSpace(compareRef))
                return (false, $"Branch '{branchName}' was never pushed and no base branch was found to undo to. Nothing was changed.");

            var (baseExit, baseOut, baseErr) = await runner.RunAsync(
                "git", ["merge-base", "--end-of-options", compareRef, "HEAD"], repoPath, null, ct, GitLockIntent.Read);
            var mergeBase = (baseOut ?? "").Trim();
            if (baseExit != 0 || mergeBase.Length == 0)
            {
                logger.LogError("Git merge-base {CompareRef} HEAD failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}", compareRef, repoPath, baseExit, baseErr);
                return (false, $"Branch '{branchName}' was never pushed and has no common commit with '{compareRef}'. Nothing was changed.");
            }

            logger.LogInformation("Remote ref origin/{BranchName} not found in {RepoPath} - undoing to merge base {MergeBase} with {CompareRef}", branchName, repoPath, mergeBase, compareRef);
            target = mergeBase;
        }

        var mode = keepChanges ? "--mixed" : "--hard";
        string[] args = ["reset", mode, target];

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await runner.RunAsync("git", args, repoPath, null, ct);
        sw.Stop();
        if (exitCode != 0)
        {
            var combined = CombineOutput(stdout, stderr) ?? "";
            logger.LogError("Git reset failed in {ElapsedMs}ms for {RepoPath}. Args={Args}, ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}",
                sw.ElapsedMilliseconds, repoPath, string.Join(' ', args), exitCode, stdout, stderr);
            return (false, combined);
        }

        logger.LogInformation("Git reset {Mode} {Target} completed in {ElapsedMs}ms for {RepoPath}", mode, target, sw.ElapsedMilliseconds, repoPath);
        return (true, null);
    }

    public async Task<GitCloneResult> CloneIntoAsync(string targetDir, string cloneUrl, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
            throw new ArgumentException("Target directory is required.", nameof(targetDir));
        if (string.IsNullOrWhiteSpace(cloneUrl))
            throw new ArgumentException("Clone URL is required.", nameof(cloneUrl));

        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        var args = $"{CloneLongPathsPrefix}clone --progress \"{cloneUrl}\" .";

        if (Directory.EnumerateFileSystemEntries(targetDir).Any())
        {
            logger.LogError("Git clone of {Url} refused: destination {Dir} is not empty.", cloneUrl, targetDir);
            return GitCloneResult.FromFailure(new GitRemoteFailure(128, GitRemoteFailureKind.Unknown,
                $"Cannot clone: the folder '{targetDir}' is not empty.", null, null, null));
        }

        // The folder was empty (or created here), so anything in it after a failed attempt is a partial clone.
        void DiscardPartialClone() => DeletePartialClone(targetDir, deleteRoot: false);

        var sw = Stopwatch.StartNew();
        var run = await runner.RunRemoteAsync(GitRemoteOperation.Clone, args, targetDir, bearerToken, ct, DiscardPartialClone);
        sw.Stop();
        if (run.ExitCode != 0)
        {
            DiscardPartialClone();
            var failure = GitHubSsoAuthorization.Describe(run.ExitCode, run.Stdout, run.Stderr, run.AuthorizationUrl);
            LogIfAuthFailure("clone", targetDir, run.Stdout, run.Stderr);
            logger.LogError("Git clone into {Dir} failed after retries in {ElapsedMs}ms. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", targetDir, sw.ElapsedMilliseconds, run.ExitCode, run.Stdout, run.Stderr);
            return GitCloneResult.FromFailure(failure);
        }
        logger.LogInformation("Git clone completed in {ElapsedMs}ms: {Url} -> {Dir}", sw.ElapsedMilliseconds, cloneUrl, targetDir);
        return GitCloneResult.Succeeded(run.ExitCode);
    }

    public async Task<(bool Success, string? Error)> InitAsync(string repoPath, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await runner.RunAsync("git", "init", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogError("Git init failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return (false, BuildProcessError(stderr, stdout, $"Git init failed (exit code {exitCode})"));
        }
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> AddRemoteAsync(string repoPath, string name, string url, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"remote add \"{name}\" \"{url}\"", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogError("Git remote add failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return (false, BuildProcessError(stderr, stdout, $"Git remote add failed (exit code {exitCode})"));
        }
        return (true, null);
    }

    public async Task<string?> GetRemoteDefaultBranchAsync(string repoPath, string? bearerToken, CancellationToken ct)
    {
        const string headRefPrefix = "ref: refs/heads/";
        var (exitCode, stdout, stderr) = await runner.RunRemoteAsync(GitRemoteOperation.LsRemote, "ls-remote --symref origin HEAD", repoPath, bearerToken, ct);
        if (exitCode != 0)
        {
            LogIfAuthFailure("ls-remote --symref", repoPath, stdout, stderr);
            logger.LogWarning("Git ls-remote --symref failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return null;
        }

        foreach (var line in (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith(headRefPrefix, StringComparison.Ordinal))
                continue;
            var name = line[headRefPrefix.Length..].Split('\t', ' ')[0];
            if (name.Length > 0)
                return name;
        }
        return null;
    }

    /// <summary>How long a repository is left alone after a repair attempt that did not repair it.</summary>
    private static readonly long OriginHeadRepairRetryMs = (long)TimeSpan.FromMinutes(15).TotalMilliseconds;

    /// <summary>Last unsuccessful repair attempt per repository path (<see cref="Environment.TickCount64"/>).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _originHeadRepairAttempts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> RepairOriginHeadAsync(string repoPath, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return false;

        // A repository whose remote cannot answer (an empty remote, a HEAD pointing at an unborn branch, a branch
        // that was not fetched) would otherwise cost a network round trip on every sync. The attempt is recorded
        // before the question is asked, so a hang or a failure is not retried until the interval has passed.
        var key = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var now = Environment.TickCount64;
        if (_originHeadRepairAttempts.TryGetValue(key, out var last) && now - last < OriginHeadRepairRetryMs)
        {
            logger.LogDebug("Skipping origin/HEAD repair for {RepoPath}: tried {AgoMs}ms ago without success", repoPath, now - last);
            return false;
        }

        _originHeadRepairAttempts[key] = now;

        try
        {
            var branch = await GetRemoteDefaultBranchAsync(repoPath, bearerToken, ct);
            if (string.IsNullOrWhiteSpace(branch) || !OriginDefaultRef.IsPlainRefName(branch))
            {
                logger.LogDebug("Remote of {RepoPath} did not name a default branch; origin/HEAD left as it is", repoPath);
                return false;
            }

            // Explicit name, so no second network call. set-head refuses a branch that has no remote-tracking
            // ref here (not fetched), which is exactly the case where repointing would only trade one dangling
            // pointer for another.
            var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"remote set-head origin {branch}", repoPath, ct);
            if (exitCode != 0)
            {
                logger.LogDebug("git remote set-head origin {Branch} did not apply for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", branch, repoPath, exitCode, stdout, stderr);
                return false;
            }

            _originHeadRepairAttempts.TryRemove(key, out _);
            logger.LogInformation("Repointed origin/HEAD to origin/{Branch} for {RepoPath}: the remote's default branch had changed", branch, repoPath);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Repairing a pointer is never a reason to fail a sync.
            logger.LogWarning(ex, "Could not repair origin/HEAD for {RepoPath}", repoPath);
            return false;
        }
    }

    public async Task<(bool Success, string? Error)> CheckoutTrackingAsync(string repoPath, string branch, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"checkout -b \"{branch}\" --track \"origin/{branch}\"", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogError("Git checkout --track failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return (false, BuildProcessError(stderr, stdout, $"Git checkout failed (exit code {exitCode})"));
        }
        return (true, null);
    }

    public async Task<(bool Success, string? Error, IReadOnlyList<string> KeptFiles)> CheckoutTrackingKeepingFilesAsync(string repoPath, string branch, CancellationToken ct)
    {
        if (!OriginDefaultRef.IsPlainRefName(branch))
            return (false, $"'{branch}' is not a valid branch name.", []);

        // The branch and the index are set without touching the working tree, so a file that is already there
        // is never overwritten. Each step is plumbing with its own argument list.
        string[][] steps =
        [
            ["update-ref", $"refs/heads/{branch}", $"refs/remotes/origin/{branch}"],
            ["symbolic-ref", "HEAD", $"refs/heads/{branch}"],
            ["branch", $"--set-upstream-to=origin/{branch}", branch],
            ["reset", "-q"],
        ];
        foreach (var step in steps)
        {
            var (exitCode, stdout, stderr) = await runner.RunAsync("git", step, repoPath, null, ct);
            if (exitCode != 0)
            {
                logger.LogError("Git {Step} failed while keeping existing files for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", step[0], repoPath, exitCode, stdout, stderr);
                return (false, BuildProcessError(stderr, stdout, $"Git {step[0]} failed (exit code {exitCode})"), []);
            }
        }

        // Without -f this writes only the files that are missing; it reports each existing file, so its exit
        // code is not the answer. The check below is.
        await runner.RunAsync("git", ["checkout-index", "-a"], repoPath, null, ct);

        var missing = await runner.RunAsync("git", ["ls-files", "--deleted"], repoPath, null, ct);
        if (missing.ExitCode != 0 || !string.IsNullOrWhiteSpace(missing.Stdout))
        {
            logger.LogError("Git checkout-index left files missing for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, missing.ExitCode, missing.Stdout, missing.Stderr);
            return (false, BuildProcessError(missing.Stderr, null, "Some files of the repository could not be written to the folder."), []);
        }

        var modified = await runner.RunAsync("git", ["-c", "core.quotepath=false", "ls-files", "--modified"], repoPath, null, ct);
        var kept = modified.ExitCode == 0
            ? (modified.Stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        logger.LogInformation("Checked out {Branch} in {RepoPath} keeping {KeptCount} existing file(s) that differ from the repository", branch, repoPath, kept.Length);
        return (true, null, kept);
    }

    public async Task<(bool Success, string? Error)> SetUnbornHeadAsync(string repoPath, string branch, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await runner.RunAsync("git", $"symbolic-ref HEAD \"refs/heads/{branch}\"", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogError("Git symbolic-ref failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return (false, BuildProcessError(stderr, stdout, $"Git symbolic-ref failed (exit code {exitCode})"));
        }
        return (true, null);
    }

    // Sync hooks have one owner, GitSyncHooks; these two members only forward to it.
    private readonly GitSyncHooks _syncHooks = new(options.Value.ListenPort, logger, runner, access);

    public Task WriteSyncHooksAsync(string repoPath, int workspaceId, int repositoryId, CancellationToken ct) =>
        _syncHooks.InstallAsync(repoPath, workspaceId, repositoryId, ct);

    public Task<SyncHookRemovalTarget> ResolveSyncHookRemovalAsync(string repoPath, CancellationToken ct) =>
        _syncHooks.ResolveRemovalTargetAsync(repoPath, ct);

    public SyncHookRemovalResult RemoveSyncHooks(SyncHookRemovalTarget target) => _syncHooks.Remove(target);
    // Remote authentication lives in GitRemoteAuth + GitProcessRunner.RunRemoteAsync: every clone, fetch, pull,
    // push and ls-remote goes through RunRemoteAsync, which takes the connector token explicitly and applies it
    // (environment transport on git 2.31+, command-line fallback otherwise). Do not run those git subcommands
    // through runner.RunAsync directly. A missing/expired token still fails fast (GIT_TERMINAL_PROMPT=0,
    // GCM_INTERACTIVE=never) and the error is prefixed with GitRemoteAuth.AuthFailureHint.

    private void LogIfAuthFailure(string operation, string path, string? stdout, string? stderr)
    {
        var output = CombineOutput(stdout, stderr);
        if (GitHubSsoAuthorization.IsSsoRequired(output))
        {
            logger.LogWarning("Git {Operation} requires GitHub SSO reauthorization for {Path}.", operation, path);
            return;
        }

        if (GitRemoteAuth.IsAuthFailure(output))
            logger.LogWarning("Git {Operation} failed authentication for {Path}. {Hint}", operation, path, GitRemoteAuth.AuthFailureHint);
    }


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

    private async Task<bool> IsAncestorAsync(string repoPath, string ancestor, string descendant, CancellationToken ct)
    {
        var (exit, _, _) = await runner.RunAsync("git", $"merge-base --is-ancestor {ancestor} {descendant}", repoPath, ct);
        return exit == 0;
    }

    /// <summary>True only when ls-remote succeeded and origin has no such branch; any failure returns false so the normal delete path decides.</summary>
    private async Task<bool> IsRemoteBranchAbsentAsync(string repoPath, string name, string? bearerToken, CancellationToken ct)
    {
        var (exit, stdout, _) = await runner.RunRemoteAsync(GitRemoteOperation.LsRemote, $"ls-remote --heads origin refs/heads/{name}", repoPath, bearerToken, ct);
        return exit == 0 && string.IsNullOrWhiteSpace(stdout);
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
