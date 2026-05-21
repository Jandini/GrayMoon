using GrayMoon.App.Models;
using GrayMoon.Common.Search;

namespace GrayMoon.App.Services;

public static class GitHubRepositorySearchMatcher
{
    public static bool Matches(GitHubRepositoryEntry repository, string? query) =>
        RepositorySearch.Matches(query, term => MatchesTerm(repository, term));

    public static bool IsValidQuery(string? query) =>
        string.IsNullOrWhiteSpace(query) || RepositorySearch.Parse(query).IsValid;

    private static bool MatchesTerm(GitHubRepositoryEntry repository, RepositorySearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            return term.Field switch
            {
                "topic" => (repository.Topics ?? string.Empty)
                    .Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                _ => true,
            };
        }

        var repoName = repository.RepositoryName ?? string.Empty;
        var ownerName = repository.OrgName ?? string.Empty;
        var topics = repository.Topics ?? string.Empty;
        var connectorName = repository.ConnectorName ?? string.Empty;

        return repoName.Contains(term.Value, StringComparison.OrdinalIgnoreCase)
               || ownerName.Contains(term.Value, StringComparison.OrdinalIgnoreCase)
               || topics.Contains(term.Value, StringComparison.OrdinalIgnoreCase)
               || connectorName.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
    }
}
