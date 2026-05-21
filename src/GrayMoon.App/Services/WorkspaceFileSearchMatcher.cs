using GrayMoon.App.Api.Endpoints;
using GrayMoon.Common.Search;

namespace GrayMoon.App.Services;

public static class WorkspaceFileSearchMatcher
{
    public static bool Matches(WorkspaceFileDto file, string? query) =>
        FilterSearchMatcher.Matches(query, term => MatchesTerm(file, term));

    private static bool MatchesTerm(WorkspaceFileDto file, FilterSearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            return term.Field switch
            {
                "repo" => (file.RepositoryName ?? string.Empty)
                    .Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        var name = file.FileName ?? string.Empty;
        var path = file.FilePath ?? string.Empty;
        var repo = file.RepositoryName ?? string.Empty;
        var haystack = $"{name} {path} {repo}";
        return haystack.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
    }
}
