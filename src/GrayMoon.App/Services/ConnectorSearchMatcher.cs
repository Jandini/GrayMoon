using GrayMoon.App.Models;
using GrayMoon.Common.Search;

namespace GrayMoon.App.Services;

public static class ConnectorSearchMatcher
{
    public static bool Matches(Connector connector, string? query) =>
        FilterSearchMatcher.Matches(query, term => MatchesTerm(connector, term));

    private static bool MatchesTerm(Connector connector, FilterSearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            return term.Field switch
            {
                "type" => connector.ConnectorType.ToString()
                    .Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                "status" => (connector.Status ?? string.Empty)
                    .Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        var typeStr = connector.ConnectorType.ToString();
        var haystack = $"{connector.ConnectorName} {connector.ApiBaseUrl} {connector.UserName} {typeStr} {connector.Status}";
        return haystack.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
    }
}
