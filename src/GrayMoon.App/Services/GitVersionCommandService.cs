using System.Text.Json;
using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

public class GitVersionCommandService
{
    private readonly ILogger<GitVersionCommandService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public GitVersionCommandService(ILogger<GitVersionCommandService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GitVersionResult?> GetVersionAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw new ArgumentException("Repository path is required.", nameof(repositoryPath));
        }

        if (!Directory.Exists(repositoryPath))
        {
            return null;
        }

        return await RunDotNetGitVersionAsync(repositoryPath, cancellationToken);
    }

    private async Task<GitVersionResult?> RunDotNetGitVersionAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        // Use dotnet-gitversion (in PATH from dotnet tool install -g) for Docker compatibility
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet-gitversion",
            Arguments = "",
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
                _logger.LogWarning("Failed to start dotnet gitversion process for {Path}", repositoryPath);
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("dotnet-gitversion failed with exit code {ExitCode} in {Path}. Stdout: {Stdout} Stderr: {Stderr}",
                    process.ExitCode, repositoryPath,
                    string.IsNullOrWhiteSpace(stdout) ? "(none)" : stdout.Trim(),
                    string.IsNullOrWhiteSpace(stderr) ? "(none)" : stderr.Trim());
                return null;
            }

            return ParseGitVersionJson(stdout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running dotnet-gitversion for {Path}", repositoryPath);
            return null;
        }
    }

    private static GitVersionResult? ParseGitVersionJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GitVersionResult>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
