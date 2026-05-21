namespace GrayMoon.Common.Search;

public sealed class RepositorySearchParseResult
{
    public bool IsValid { get; init; } = true;
    public RepositorySearchNode? Expression { get; init; }
    public string? ErrorMessage { get; init; }

    public static RepositorySearchParseResult Success(RepositorySearchNode? expression) =>
        new() { IsValid = true, Expression = expression };

    public static RepositorySearchParseResult Failure(string message) =>
        new() { IsValid = false, ErrorMessage = message };
}
