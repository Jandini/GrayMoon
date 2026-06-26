using GrayMoon.Common.Search;

namespace GrayMoon.App.Services;

public static class WorkspaceRepositoryNameSearchMatcher
{
    public static bool IsValidQuery(string? query) => FilterSearchMatcher.IsValidQuery(query);

    public static bool Matches(string? repositoryName, string? query) =>
        FilterSearchMatcher.Matches(query, term =>
            (repositoryName ?? string.Empty).Contains(term.Value, StringComparison.OrdinalIgnoreCase));
}
