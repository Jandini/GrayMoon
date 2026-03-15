using System.Diagnostics;
using System.Text;
using GrayMoon.Common;
using Polly;
using Polly.Retry;

namespace GrayMoon.App.Services;

public class GitCommandService(ILogger<GitCommandService> logger)
{
    public async Task<bool> CloneAsync(string workingDirectory, string cloneUrl, string? bearerToken = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("Working directory is required.", nameof(workingDirectory));
        }

        if (string.IsNullOrWhiteSpace(cloneUrl))
        {
            throw new ArgumentException("Clone URL is required.", nameof(cloneUrl));
        }

        if (!Directory.Exists(workingDirectory))
        {
            Directory.CreateDirectory(workingDirectory);
        }

        var arguments = BuildCloneArguments(cloneUrl, bearerToken);
        var sw = Stopwatch.StartNew();
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                sw.Stop();
                logger.LogDebug("Command {Executable} {Parameters} completed in {ElapsedMs}ms (ExitCode=-1)", startInfo.FileName, LogSafe.ForLog(arguments), sw.ElapsedMilliseconds);
                logger.LogError("Failed to start git process.");
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            sw.Stop();

            logger.LogDebug("Command {Executable} {Parameters} completed in {ElapsedMs}ms (ExitCode={ExitCode})", startInfo.FileName, LogSafe.ForLog(arguments), sw.ElapsedMilliseconds, process.ExitCode);

            if (process.ExitCode != 0)
            {
                logger.LogWarning("Git clone failed with exit code {ExitCode}. Stdout: {Stdout} Stderr: {Stderr}",
                    process.ExitCode,
                    string.IsNullOrWhiteSpace(stdout) ? "(none)" : stdout.Trim(),
                    string.IsNullOrWhiteSpace(stderr) ? "(none)" : stderr.Trim());
                return false;
            }

            logger.LogInformation("Git clone completed: {Url} into {Directory}", cloneUrl, workingDirectory);
            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogDebug("Command {Executable} {Parameters} completed in {ElapsedMs}ms (ExitCode=-1)", startInfo.FileName, LogSafe.ForLog(arguments), sw.ElapsedMilliseconds);
            logger.LogError(ex, "Error running git clone for {Url}", cloneUrl);
            throw;
        }
    }

    /// <summary>
    /// Adds the repository path to git's safe.directory (repo-local config) so git commands succeed when the repo is owned by another user (e.g. in containers).
    /// Uses rev-parse to detect safety: exit 0 = safe, exit 128 with dubious ownership = not safe. Adds only when not safe.
    /// </summary>
    public async Task AddSafeDirectoryAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            return;

        var fullPath = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathForGit = fullPath.Replace('\\', '/'); // Git accepts forward slashes on all platforms

        var (isSafe, _) = await CheckRepoSafeAsync(repositoryPath, pathForGit, cancellationToken);
        logger.LogDebug("Git repo safety check: {Path} -> {Result}", pathForGit, isSafe ? "safe" : "not safe");

        if (isSafe)
        {
            logger.LogDebug("Repository already safe, skipping safe.directory update: {Path}", pathForGit);
            return;
        }

        var arguments = $"config --local --add safe.directory \"{pathForGit.Replace("\"", "\\\"")}\"";
        var (exitCode, _, stderr) = await SafeDirectoryRetryPipeline.ExecuteAsync(
            async (ct) => await RunGitAsync("git", arguments, repositoryPath, ct),
            cancellationToken);

        if (exitCode == 0)
            logger.LogDebug("Added safe.directory for repository: {Path}", pathForGit);
        else
            logger.LogDebug("Git config safe.directory returned {ExitCode} for {Path}", exitCode, pathForGit);
    }

    /// <summary>
    /// Uses git rev-parse --is-inside-work-tree: exit 0 = repo is safe, exit 128 with dubious ownership = not safe.
    /// </summary>
    private async Task<(bool IsSafe, bool IsDubiousOwnership)> CheckRepoSafeAsync(string repositoryPath, string pathForGit, CancellationToken cancellationToken)
    {
        var (exitCode, _, stderr) = await RunGitAsync("git", "rev-parse --is-inside-work-tree", repositoryPath, cancellationToken);
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

    /// <summary>Runs a git command with DEBUG logging once after completion (safe parameters, elapsed ms). Tokens are not logged.</summary>
    private async Task<(int ExitCode, string? Stdout, string? Stderr)> RunGitAsync(string fileName, string arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process == null)
        {
            sw.Stop();
            logger.LogDebug("Command {Executable} {Parameters} completed in {ElapsedMs}ms (ExitCode=-1)", startInfo.FileName, LogSafe.ForLog(arguments), sw.ElapsedMilliseconds);
            return (-1, null, "Failed to start process");
        }
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        logger.LogDebug("Command {Executable} {Parameters} completed in {ElapsedMs}ms (ExitCode={ExitCode})", fileName, LogSafe.ForLog(arguments), sw.ElapsedMilliseconds, process.ExitCode);
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Builds git clone arguments. When bearerToken is set, uses -c http.extraHeader with Basic auth for private HTTPS repos.
    /// GitHub expects Basic auth (x-access-token:TOKEN or username:TOKEN), not Bearer, for git clone.
    /// </summary>
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

    public async Task<string?> GetHeadShaAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return null;
        }

        var (exitCode, stdout, stderr) = await RunGitAsync("git", "rev-parse HEAD", repositoryPath, cancellationToken);

        if (exitCode != 0)
        {
            logger.LogWarning("Git rev-parse HEAD failed with exit code {ExitCode} in {Path}. Stdout: {Stdout} Stderr: {Stderr}",
                exitCode, repositoryPath,
                string.IsNullOrWhiteSpace(stdout) ? "(none)" : stdout.Trim(),
                string.IsNullOrWhiteSpace(stderr) ? "(none)" : stderr.Trim());
            return null;
        }

        return stdout?.Trim();
    }
}
