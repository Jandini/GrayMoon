using System.Text.Json;

namespace GrayMoon.App.Services;

public class GitVersionCommandService
{
    private readonly ILogger<GitVersionCommandService> _logger;

    public GitVersionCommandService(ILogger<GitVersionCommandService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> GetVersionAsync(string repositoryPath, CancellationToken cancellationToken = default)
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

            return ParseSemVerFromJson(stdout);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error running dotnet gitversion for {Path}", repositoryPath);
            return null;
        }
    }

    private static string? ParseSemVerFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("SemVer", out var semVer))
            {
                return semVer.GetString();
            }

            if (root.TryGetProperty("FullSemVer", out var fullSemVer))
            {
                return fullSemVer.GetString();
            }
        }
        catch (JsonException)
        {
            // Not valid JSON or unexpected structure
        }

        return null;
    }
}
