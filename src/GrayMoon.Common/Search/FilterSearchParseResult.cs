namespace GrayMoon.Common.Search;

public sealed class FilterSearchParseResult
{
    public bool IsValid { get; init; } = true;
    public FilterSearchNode? Expression { get; init; }
    public string? ErrorMessage { get; init; }

    public static FilterSearchParseResult Success(FilterSearchNode? expression) =>
        new() { IsValid = true, Expression = expression };

    public static FilterSearchParseResult Failure(string message) =>
        new() { IsValid = false, ErrorMessage = message };
}
