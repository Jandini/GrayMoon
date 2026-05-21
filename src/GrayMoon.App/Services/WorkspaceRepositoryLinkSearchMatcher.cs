using GrayMoon.App.Models;
using GrayMoon.Common.Search;

namespace GrayMoon.App.Services;

public static class WorkspaceRepositoryLinkSearchMatcher
{
    public static bool IsValidQuery(string? query) => RepositorySearch.IsValidQuery(query);

    public static bool Matches(
        WorkspaceRepositoryLink link,
        string? query,
        IReadOnlyDictionary<int, RepoSyncStatus>? syncStatusByRepositoryId = null) =>
        RepositorySearch.Matches(query, term => MatchesTerm(link, term, syncStatusByRepositoryId));

    private static bool MatchesTerm(
        WorkspaceRepositoryLink link,
        RepositorySearchTerm term,
        IReadOnlyDictionary<int, RepoSyncStatus>? syncStatusByRepositoryId)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            return term.Field switch
            {
                "topic" => false,
                _ => true,
            };
        }

        var repoName = link.Repository?.RepositoryName ?? string.Empty;
        var branchName = link.BranchName ?? string.Empty;
        var version = link.GitVersion ?? string.Empty;
        var levelTitle = link.DependencyLevel == null ? "No dependencies" : $"Level {link.DependencyLevel}";
        var status = syncStatusByRepositoryId?.TryGetValue(link.RepositoryId, out var s) == true
            ? s
            : link.SyncStatus;
        var syncText = SyncBadgeLabels.GetSyncBadgeText(status);
        var searchable = $"{repoName} {branchName} {version} {levelTitle} {syncText}";

        return searchable.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
    }
}
