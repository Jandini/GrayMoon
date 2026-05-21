using GrayMoon.App.Models;
using GrayMoon.Common.Search;

namespace GrayMoon.App.Services;

public static class WorkspacePackageSearchMatcher
{
    public static bool Matches(WorkspaceProject package, string? query) =>
        FilterSearchMatcher.Matches(query, term => MatchesTerm(package, term));

    private static bool MatchesTerm(WorkspaceProject package, FilterSearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            return term.Field switch
            {
                "registry" => (package.MatchedConnector?.ConnectorName ?? string.Empty)
                    .Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                "framework" => (package.TargetFramework ?? string.Empty)
                    .Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        var id = package.PackageId ?? string.Empty;
        var haystack = $"{id} {package.TargetFramework} {package.MatchedConnector?.ConnectorName}";
        return haystack.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
    }
}
