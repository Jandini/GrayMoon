namespace GrayMoon.App.Models;

/// <summary>Result of refreshing repositories from GitHub; may include per-connector fetch errors and detected renames.</summary>
public sealed class RefreshRepositoriesResult
{
    public IReadOnlyList<GitHubRepositoryEntry> Repositories { get; init; } = [];
    public IReadOnlyList<ConnectorFetchError> ConnectorErrors { get; init; } = [];
    public IReadOnlyList<RenamedRepositoryInfo> RenamedRepositories { get; init; } = [];
}

/// <summary>Error for a single connector when fetching its repositories.</summary>
public sealed class ConnectorFetchError
{
    public string ConnectorName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>Describes a repository that was renamed in GitHub and updated in place during a fetch.</summary>
public sealed class RenamedRepositoryInfo
{
    public string OldName { get; init; } = string.Empty;
    public string NewName { get; init; } = string.Empty;
    public string? OrgName { get; init; }
}
