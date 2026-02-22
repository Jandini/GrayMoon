using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace GrayMoon.Agent.Services;

public sealed class GitService(IOptions<AgentOptions> options, ILogger<GitService> logger) : IGitService
{
    private readonly string _workspaceRoot = GetWorkspaceRoot(options.Value.WorkspaceRoot);
    private readonly int _listenPort = options.Value.ListenPort;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string GetWorkspaceRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return OperatingSystem.IsWindows() ? @"C:\Workspace" : "/var/graymoon/workspaces";
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public string WorkspaceRoot => _workspaceRoot;

    public string GetWorkspacePath(string workspaceName)
    {
        var safe = SanitizeDirectoryName(workspaceName);
        return Path.Combine(_workspaceRoot, safe);
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
        var (exitCode, stdout, stderr) = await RunProcessAsync("git", args, workingDir, ct);
        if (exitCode != 0)
        {
            logger.LogError("Git clone failed. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", exitCode, stdout, stderr);
            return false;
        }
        logger.LogInformation("Git clone completed: {Url} -> {Dir}", cloneUrl, workingDir);
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

    private static readonly ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> SafeDirectoryRetryPipeline =
        new ResiliencePipelineBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
            .AddRetry(new RetryStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)>
            {
                ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>().HandleResult(r => r.ExitCode != 0),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(100),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true
            })
            .Build();

    public async Task<GitVersionResult?> GetVersionAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        var (exitCode, stdout, stderr) = await RunProcessAsync("dotnet-gitversion", "", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogError("dotnet-gitversion failed. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", exitCode, stdout, stderr);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GitVersionResult>(stdout ?? "", JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
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

    public async Task FetchAsync(string repoPath, bool includeTags, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return;

        string args;
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            // Use --prune to remove stale remote-tracking branches
            args = includeTags ? "fetch origin --prune --tags" : "fetch origin --prune";
        }
        else
        {
            var credentials = "x-access-token:" + bearerToken;
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            var headerValue = "Authorization: Basic " + base64;
            var escaped = headerValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
            // Use --prune to remove stale remote-tracking branches
            var fetchCmd = includeTags ? "fetch origin --prune --tags" : "fetch origin --prune";
            args = $"-c \"http.extraHeader={escaped}\" {fetchCmd}";
        }
        var (exitCode, stdout, stderr) = await RunProcessAsync("git", args, repoPath, ct);
        if (exitCode != 0)
            logger.LogError("Git fetch failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
    }

    public async Task<(int? Outgoing, int? Incoming)> GetCommitCountsAsync(string repoPath, string branchName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (null, null);

        var originBranch = "origin/" + branchName.Trim();
        
        // Check if the remote branch exists locally (after fetch) before trying to count commits
        // This is faster than ls-remote and works since we fetch before calling this method
        var (exitCheck, _, _) = await RunProcessAsync("git", $"rev-parse --verify {originBranch}", repoPath, ct);
        if (exitCheck != 0)
        {
            // Branch doesn't exist upstream yet - count commits ahead of the default branch instead
            var defaultBranch = await GetDefaultBranchAsync(repoPath, ct);
            if (defaultBranch == null)
            {
                logger.LogDebug("Remote branch {OriginBranch} does not exist upstream and no default branch found for {RepoPath}, skipping commit counts", originBranch, repoPath);
                return (null, null);
            }

            // Count commits ahead of the default branch
            var (exitDefault, stdoutDefault, stderrDefault) = await RunProcessAsync("git", $"rev-list --count {defaultBranch}..HEAD", repoPath, ct);
            if (exitDefault != 0)
            {
                logger.LogWarning("Git rev-list (outgoing vs default branch) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitDefault, stdoutDefault, stderrDefault);
                return (null, null);
            }

            var aheadCount = int.TryParse((stdoutDefault ?? "").Trim(), out var ahead) ? ahead : (int?)null;
            // No incoming commits for a branch that doesn't exist upstream
            return (aheadCount, null);
        }

        // Branch exists upstream - use standard comparison
        var (exitOut, stdoutOut, stderrOut) = await RunProcessAsync("git", $"rev-list --count {originBranch}..HEAD", repoPath, ct);
        var (exitIn, stdoutIn, stderrIn) = await RunProcessAsync("git", $"rev-list --count HEAD..{originBranch}", repoPath, ct);
        
        // If either command fails, return null values gracefully (don't break sync)
        if (exitOut != 0)
        {
            logger.LogWarning("Git rev-list (outgoing) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitOut, stdoutOut, stderrOut);
            return (null, null);
        }
        if (exitIn != 0)
        {
            logger.LogWarning("Git rev-list (incoming) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitIn, stdoutIn, stderrIn);
            return (null, null);
        }

        var outVal = int.TryParse((stdoutOut ?? "").Trim(), out var o) ? o : (int?)null;
        var inVal = int.TryParse((stdoutIn ?? "").Trim(), out var i) ? i : (int?)null;
        return (outVal, inVal);
    }

    public async Task<(bool Success, bool MergeConflict, string? ErrorMessage)> PullAsync(string repoPath, string branchName, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, false, "Invalid repository path or branch name");

        string args;
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            args = $"pull origin {branchName}";
        }
        else
        {
            var credentials = "x-access-token:" + bearerToken;
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            var headerValue = "Authorization: Basic " + base64;
            var escaped = headerValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
            args = $"-c \"http.extraHeader={escaped}\" pull origin {branchName}";
        }

        var (exitCode, stdout, stderr) = await RunProcessAsync("git", args, repoPath, ct);
        
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
            
            logger.LogError("Git pull failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return (false, false, combinedOutput);
        }

        logger.LogInformation("Git pull completed for {RepoPath}. Branch={Branch}", repoPath, branchName);
        return (true, false, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> PushAsync(string repoPath, string branchName, string? bearerToken, bool setTracking = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || string.IsNullOrWhiteSpace(branchName))
            return (false, "Invalid repository path or branch name");

        var pushOpts = setTracking ? "-u " : "";
        string args;
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            args = $"push {pushOpts}origin {branchName}";
        }
        else
        {
            var credentials = "x-access-token:" + bearerToken;
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            var headerValue = "Authorization: Basic " + base64;
            var escaped = headerValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
            args = $"-c \"http.extraHeader={escaped}\" push {pushOpts}origin {branchName}";
        }

        var (exitCode, stdout, stderr) = await RunProcessAsync("git", args, repoPath, ct);
        if (exitCode != 0)
        {
            // Combine stdout and stderr for error message
            var output = (stdout ?? "").Trim();
            var errorOutput = (stderr ?? "").Trim();
            var combinedOutput = string.IsNullOrWhiteSpace(output) ? errorOutput : 
                                 string.IsNullOrWhiteSpace(errorOutput) ? output : 
                                 $"{output}\n{errorOutput}";
            
            logger.LogError("Git push failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return (false, combinedOutput);
        }

        logger.LogInformation("Git push completed for {RepoPath}. Branch={Branch}", repoPath, branchName);
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

    public async Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return Array.Empty<string>();

        // Use ls-remote to query the actual remote branches (not local remote-tracking refs)
        // This ensures we only see branches that actually exist on origin, not stale local references
        var (exitCode, stdout, stderr) = await RunProcessAsync("git", "ls-remote --heads origin", repoPath, ct);
        if (exitCode != 0)
        {
            logger.LogWarning("Git ls-remote failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
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

        // Ensure we're on the base branch (checkout base first)
        var (checkoutSuccess, checkoutError) = await CheckoutBranchAsync(repoPath, baseBranchName, ct);
        if (!checkoutSuccess)
            return (false, checkoutError ?? "Failed to checkout base branch");

        // Create and checkout new branch from current HEAD
        var (exitCode, stdout, stderr) = await RunProcessAsync("git", $"checkout -b {newBranchName}", repoPath, ct);
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

    /// <summary>
    /// Finds the default branch (main or master) on origin. Returns null if neither exists.
    /// </summary>
    private async Task<string?> GetDefaultBranchAsync(string repoPath, CancellationToken ct)
    {
        // Try main first (most common modern default)
        var (exitMain, _, _) = await RunProcessAsync("git", "rev-parse --verify origin/main", repoPath, ct);
        if (exitMain == 0)
            return "origin/main";

        // Fall back to master
        var (exitMaster, _, _) = await RunProcessAsync("git", "rev-parse --verify origin/master", repoPath, ct);
        if (exitMaster == 0)
            return "origin/master";

        // Try to get the default branch from remote HEAD
        var (exitHead, stdoutHead, _) = await RunProcessAsync("git", "symbolic-ref refs/remotes/origin/HEAD", repoPath, ct);
        if (exitHead == 0 && !string.IsNullOrWhiteSpace(stdoutHead))
        {
            var refName = stdoutHead.Trim();
            if (refName.StartsWith("refs/remotes/origin/"))
            {
                var branch = refName.Substring("refs/remotes/origin/".Length);
                var (exitVerify, _, _) = await RunProcessAsync("git", $"rev-parse --verify origin/{branch}", repoPath, ct);
                if (exitVerify == 0)
                    return $"origin/{branch}";
            }
        }

        return null;
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

        var notifyUrl = $"http://127.0.0.1:{_listenPort}/notify";
        var payload = JsonSerializer.Serialize(new { repositoryId, workspaceId, repositoryPath = repoPath });
        var curlLine = $"curl -s -X POST \"{notifyUrl}\" -H \"Content-Type: application/json\" -d '{payload.Replace("'", "'\\''")}'";

        var utf8 = new UTF8Encoding(false);
        var comment = $"# Created by GrayMoon.Agent at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n";

        WriteHookFile(Path.Combine(hooksDir, "post-commit"), "#!/bin/sh\n" + comment + curlLine + "\n", utf8);
        WriteHookFile(Path.Combine(hooksDir, "post-checkout"), "#!/bin/sh\n" + comment + "[ \"$3\" = \"1\" ] && " + curlLine.TrimEnd() + "\n", utf8);
        WriteHookFile(Path.Combine(hooksDir, "post-merge"), "#!/bin/sh\n" + comment + curlLine + "\n", utf8);
        WriteHookFile(Path.Combine(hooksDir, "post-update"), "#!/bin/sh\n" + comment + curlLine + "\n", utf8);
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
        return $"-c \"http.extraHeader={escaped}\" clone \"{cloneUrl}\"";
    }

    private static string SanitizeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "workspace";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Trim().Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "workspace" : sanitized;
    }

    private static async Task<(int ExitCode, string? Stdout, string? Stderr)> RunProcessAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            return (-1, null, "Failed to start process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<(int ExitCode, string? Stdout, string? Stderr)> RunProcessWithStdinAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        string stdinContent,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            return (-1, null, "Failed to start process");

        await process.StandardInput.WriteAsync(stdinContent.AsMemory(), ct);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }
}
