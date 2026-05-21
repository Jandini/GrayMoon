namespace GrayMoon.Common.Search;

/// <summary>Shared helpers for evaluating <see cref="FilterSearchExpression"/> queries.</summary>
public static class FilterSearchMatcher
{
    public static bool IsValidQuery(string? query) => FilterSearchExpression.IsValidQuery(query);

    public static bool Matches(string? query, Func<FilterSearchTerm, bool> matchTerm) =>
        FilterSearchExpression.Matches(query, matchTerm);

    public static bool MatchesHaystack(string? query, string haystack) =>
        Matches(query, term => haystack.Contains(term.Value, StringComparison.OrdinalIgnoreCase));
}
