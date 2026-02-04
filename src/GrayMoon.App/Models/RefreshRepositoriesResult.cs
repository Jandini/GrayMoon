namespace GrayMoon.App.Models;

/// <summary>Result of refreshing repositories from GitHub; may include per-connector fetch errors.</summary>
public sealed class RefreshRepositoriesResult
{
    public IReadOnlyList<GitHubRepositoryEntry> Repositories { get; init; } = [];
    public IReadOnlyList<ConnectorFetchError> ConnectorErrors { get; init; } = [];
}

/// <summary>Error for a single connector when fetching its repositories.</summary>
public sealed class ConnectorFetchError
{
    public string ConnectorName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
