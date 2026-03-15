using System.Text.Json;
using GrayMoon.App.Models;
using GrayMoon.Common;

namespace GrayMoon.App.Services;

public class GitVersionCommandService(ILogger<GitVersionCommandService> logger, ICommandLineService commandLine)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

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
        try
        {
            var result = await commandLine.RunAsync("dotnet-gitversion", "/output json /nofetch /verbosity quiet", repositoryPath, null, cancellationToken);

            if (result.ExitCode == -1)
            {
                logger.LogWarning("Failed to start dotnet gitversion process for {Path}", repositoryPath);
                return null;
            }
            if (result.ExitCode != 0)
            {
                logger.LogWarning("dotnet-gitversion failed with exit code {ExitCode} in {Path}. Stdout: {Stdout} Stderr: {Stderr}",
                    result.ExitCode, repositoryPath,
                    string.IsNullOrWhiteSpace(result.Stdout) ? "(none)" : result.Stdout.Trim(),
                    string.IsNullOrWhiteSpace(result.Stderr) ? "(none)" : result.Stderr.Trim());
                return null;
            }

            var parsed = ParseGitVersionJson(result.Stdout);
            if (parsed != null)
            {
                logger.LogInformation("GitVersion for {Path}: {InformationalVersion} ({Branch})", repositoryPath,
                    parsed.InformationalVersion ?? "-", parsed.BranchName ?? parsed.EscapedBranchName ?? "-");
            }
            return parsed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running dotnet-gitversion for {Path}", repositoryPath);
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
