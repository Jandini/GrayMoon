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

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "gitversion",
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

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                _logger.LogDebug("GitVersion failed for {Path}: exit {Code}, {Stderr}", repositoryPath, process.ExitCode, stderr);
                return null;
            }

            return ParseGitVersionJson(stdout);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error running dotnet gitversion for {Path}", repositoryPath);
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
