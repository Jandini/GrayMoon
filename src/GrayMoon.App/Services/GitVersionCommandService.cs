using System.Text.Json;
using GrayMoon.App.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GrayMoon.App.Services;

public class GitVersionCommandService
{
    private readonly ILogger<GitVersionCommandService> _logger;
    private readonly GitCommandService _gitCommandService;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public GitVersionCommandService(
        ILogger<GitVersionCommandService> logger,
        GitCommandService gitCommandService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _gitCommandService = gitCommandService ?? throw new ArgumentNullException(nameof(gitCommandService));
    }

    public async Task<GitVersionResult?> GetVersionAsync(string repositoryPath, CancellationToken cancellationToken = default)
        => await GetVersionAsync(repositoryPath, useCacheIfAvailable: false, cancellationToken);

    public async Task<GitVersionResult?> GetVersionAsync(string repositoryPath, bool useCacheIfAvailable, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw new ArgumentException("Repository path is required.", nameof(repositoryPath));
        }

        if (!Directory.Exists(repositoryPath))
        {
            return null;
        }

        if (useCacheIfAvailable)
        {
            var cached = await TryGetVersionFromCacheAsync(repositoryPath, cancellationToken);
            if (cached != null)
            {
                _logger.LogDebug("Using GitVersion cache for {Path}", repositoryPath);
                return cached;
            }
        }

        return await RunDotNetGitVersionAsync(repositoryPath, cancellationToken);
    }

    private async Task<GitVersionResult?> TryGetVersionFromCacheAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var cacheDir = Path.Combine(repositoryPath, ".git", "gitversion_cache");
        if (!Directory.Exists(cacheDir))
        {
            return null;
        }

        var headSha = await _gitCommandService.GetHeadShaAsync(repositoryPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(headSha))
        {
            return null;
        }

        var yamlFiles = Directory.GetFiles(cacheDir, "*.yml");
        foreach (var file in yamlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var yaml = await File.ReadAllTextAsync(file, cancellationToken);
                var result = ParseGitVersionYaml(yaml);
                if (result != null && string.Equals(result.Sha, headSha, StringComparison.OrdinalIgnoreCase))
                {
                    return result;
                }
            }
            catch
            {
                // Skip malformed or unreadable cache files
            }
        }

        return null;
    }

    private static GitVersionResult? ParseGitVersionYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return null;
        }

        try
        {
            var dict = YamlDeserializer.Deserialize<Dictionary<string, object>>(yaml);
            if (dict == null)
            {
                return null;
            }

            return new GitVersionResult
            {
                SemVer = GetString(dict, "SemVer"),
                FullSemVer = GetString(dict, "FullSemVer"),
                BranchName = GetString(dict, "BranchName"),
                EscapedBranchName = GetString(dict, "EscapedBranchName"),
                Major = GetInt(dict, "Major"),
                Minor = GetInt(dict, "Minor"),
                Patch = GetInt(dict, "Patch"),
                PreReleaseTag = GetString(dict, "PreReleaseTag"),
                Sha = GetString(dict, "Sha"),
                ShortSha = GetString(dict, "ShortSha"),
                InformationalVersion = GetString(dict, "InformationalVersion")
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }
        return value.ToString()?.Trim();
    }

    private static int GetInt(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
        {
            return 0;
        }
        if (value is int i)
            return i;
        if (value is long l)
            return (int)l;
        return int.TryParse(value.ToString(), out var n) ? n : 0;
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
