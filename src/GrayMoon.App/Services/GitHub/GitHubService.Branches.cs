using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GrayMoon.App.Models;

namespace GrayMoon.App.Services.GitHub;

/// <summary>Remote branch names of one repository and the branch GitHub calls its default.</summary>
/// <param name="DefaultBranch">Null when GitHub did not name one (for example an empty repository).</param>
/// <param name="Branches">Every branch read, in GitHub's order.</param>
/// <param name="Truncated">True when the repository has more branches than <see cref="GitHubService.MaxBranchPages"/> pages hold.</param>
public sealed record GitHubBranchList(string? DefaultBranch, IReadOnlyList<string> Branches, bool Truncated);

public sealed partial class GitHubService
{
    internal const int MaxBranchPages = 20;
    private const int BranchPageSize = 100;

    /// <summary>
    /// Lists the branches of a repository (paginated, never silently cut: <see cref="GitHubBranchList.Truncated"/>
    /// says so) and reads its default branch. Uses the connector token and the shared HTTP pipeline.
    /// </summary>
    public async Task<GitHubBranchList> GetRepositoryBranchesAsync(Connector connector, string owner, string repo, CancellationToken cancellationToken = default)
    {
        EnsureConnectorConfigured(connector);
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository is required.", nameof(repo));

        var defaultBranch = (await GetJsonAsync<GitHubBranchRepositoryInfo>(connector, $"repos/{owner}/{repo}", cancellationToken))?.DefaultBranch;

        var names = new List<string>();
        var truncated = false;
        for (var page = 1; ; page++)
        {
            var items = await GetJsonAsync<List<GitHubBranchItem>>(
                connector, $"repos/{owner}/{repo}/branches?per_page={BranchPageSize}&page={page}", cancellationToken);
            if (items is null || items.Count == 0)
                break;

            names.AddRange(items.Where(i => !string.IsNullOrEmpty(i.Name)).Select(i => i.Name!));
            if (items.Count < BranchPageSize)
                break;
            if (page >= MaxBranchPages)
            {
                truncated = true;
                break;
            }
        }

        return new GitHubBranchList(string.IsNullOrWhiteSpace(defaultBranch) ? null : defaultBranch, names, truncated);
    }

    private async Task<T?> GetJsonAsync<T>(Connector connector, string requestUri, CancellationToken cancellationToken)
    {
        using var response = await GetResponseAsync(connector, requestUri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("GitHub API call failed. Status: {StatusCode}, URL: {Url}, Response: {Response}",
                response.StatusCode,
                response.RequestMessage?.RequestUri,
                errorContent);
            ThrowGitHubApiFailure(response, errorContent);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken);
    }

    private sealed class GitHubBranchRepositoryInfo
    {
        [JsonPropertyName("default_branch")]
        public string? DefaultBranch { get; set; }
    }

    private sealed class GitHubBranchItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
