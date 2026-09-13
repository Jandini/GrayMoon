using GrayMoon.Abstractions.Models;
using GrayMoon.App.Models;

namespace GrayMoon.App.Services.GitHub;

/// <summary>Organization and repository listing (paged) for both the legacy single-PAT path and per-connector calls.</summary>
public sealed partial class GitHubService
{
    public async Task<List<GitHubOrganizationDto>> GetOrganizationsAsync()
    {
        EnsureConfigured();
        return await GetAsync<List<GitHubOrganizationDto>>("user/orgs") ?? new List<GitHubOrganizationDto>();
    }

    public async Task<List<GitHubRepositoryDto>> GetRepositoriesAsync()
    {
        EnsureConfigured();
        return await GetRepositoriesPagedAsync("user/repos?visibility=all");
    }

    public async Task<List<GitHubRepositoryDto>> GetRepositoriesAsync(Connector connector, IProgress<int>? progress = null, IProgress<IReadOnlyList<GitHubRepositoryDto>>? batchProgress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnectorConfigured(connector);
        return await GetRepositoriesPagedAsync(connector, "user/repos?visibility=all", progress, batchProgress, cancellationToken);
    }

    private async Task<List<GitHubRepositoryDto>> GetRepositoriesPagedAsync(string requestUri)
    {
        var results = new List<GitHubRepositoryDto>();
        const int pageSize = 100;
        var page = 1;

        while (true)
        {
            var pageUri = $"{requestUri}&per_page={pageSize}&page={page}";
            var pageItems = await GetAsync<List<GitHubRepositoryDto>>(pageUri) ?? new List<GitHubRepositoryDto>();

            results.AddRange(pageItems);

            if (pageItems.Count < pageSize)
            {
                break;
            }

            page++;
        }

        return results;
    }

    private async Task<List<GitHubRepositoryDto>> GetRepositoriesPagedAsync(Connector connector, string requestUri, IProgress<int>? progress = null, IProgress<IReadOnlyList<GitHubRepositoryDto>>? batchProgress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<GitHubRepositoryDto>();
        const int pageSize = 100;
        var page = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageUri = $"{requestUri}&per_page={pageSize}&page={page}";
            var pageItems = await GetAsync<List<GitHubRepositoryDto>>(connector, pageUri, cancellationToken) ?? new List<GitHubRepositoryDto>();

            results.AddRange(pageItems);
            progress?.Report(results.Count);
            if (pageItems.Count > 0)
                batchProgress?.Report(pageItems);

            if (pageItems.Count < pageSize)
            {
                break;
            }

            page++;
        }

        return results;
    }
}
