namespace GrayMoon.App.Services;

public class GitCommandService
{
    private readonly ILogger<GitCommandService> _logger;

    public GitCommandService(ILogger<GitCommandService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
                _logger.LogError("Failed to start git process.");
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Git clone failed with exit code {ExitCode}. Stdout: {Stdout} Stderr: {Stderr}",
                    process.ExitCode,
                    string.IsNullOrWhiteSpace(stdout) ? "(none)" : stdout.Trim(),
                    string.IsNullOrWhiteSpace(stderr) ? "(none)" : stderr.Trim());
                return false;
            }

            _logger.LogInformation("Git clone completed: {Url} into {Directory}", cloneUrl, workingDirectory);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running git clone for {Url}", cloneUrl);
            throw;
        }
    }

    /// <summary>
    /// Builds git clone arguments. When bearerToken is set, uses -c http.extraHeader="Authorization: Bearer TOKEN" for private HTTPS repos.
    /// </summary>
    private static string BuildCloneArguments(string cloneUrl, string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            return $"clone \"{cloneUrl}\"";

        var escapedToken = bearerToken.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"-c \"http.extraHeader=Authorization: Bearer {escapedToken}\" clone \"{cloneUrl}\"";
    }

    public async Task<string?> GetHeadShaAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return null;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse HEAD",
            WorkingDirectory = repositoryPath,
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
                _logger.LogWarning("Failed to start git process for rev-parse HEAD in {Path}", repositoryPath);
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Git rev-parse HEAD failed with exit code {ExitCode} in {Path}. Stdout: {Stdout} Stderr: {Stderr}",
                    process.ExitCode, repositoryPath,
                    string.IsNullOrWhiteSpace(stdout) ? "(none)" : stdout.Trim(),
                    string.IsNullOrWhiteSpace(stderr) ? "(none)" : stderr.Trim());
                return null;
            }

            return stdout.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running git rev-parse HEAD in {Path}", repositoryPath);
            return null;
        }
    }
}
