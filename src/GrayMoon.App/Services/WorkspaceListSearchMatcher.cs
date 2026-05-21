using GrayMoon.App.Models;
using GrayMoon.Common.Search;

namespace GrayMoon.App.Services;

public static class WorkspaceListSearchMatcher
{
    public static bool Matches(Workspace workspace, string displayPath, string? query) =>
        FilterSearchMatcher.Matches(query, term => MatchesTerm(workspace, displayPath, term));

    private static bool MatchesTerm(Workspace workspace, string displayPath, FilterSearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            return term.Field switch
            {
                "path" => displayPath.Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        var repoCount = workspace.Repositories.Count;
        var projectCount = workspace.Repositories.Sum(r => r.Projects ?? 0);
        var haystack = $"{workspace.Name} {displayPath} {repoCount} {projectCount}";
        return haystack.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
    }
}
