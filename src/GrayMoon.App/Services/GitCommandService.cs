namespace GrayMoon.App.Services;

public class GitCommandService
{
    private readonly ILogger<GitCommandService> _logger;

    public GitCommandService(ILogger<GitCommandService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> CloneAsync(string workingDirectory, string cloneUrl, CancellationToken cancellationToken = default)
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

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"clone \"{cloneUrl}\"",
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

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                _logger.LogWarning("Git clone failed with exit code {ExitCode}: {Stderr}", process.ExitCode, stderr);
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
}
