namespace GrayMoon.App.Models;

/// <summary>Result of refreshing repositories from GitHub; may include per-connector fetch errors and detected renames.</summary>
public sealed class RefreshRepositoriesResult
{
    public IReadOnlyList<ConnectorFetchError> ConnectorErrors { get; init; } = [];
    public IReadOnlyList<RenamedRepositoryInfo> RenamedRepositories { get; init; } = [];
    /// <summary>Maps old (deleted) RepositoryId to the surviving canonical RepositoryId for any repository that was merged during rename reconciliation. Empty when no merges occurred.</summary>
    public IReadOnlyDictionary<int, int> MergedRepositoryIdMap { get; init; } = new Dictionary<int, int>();
    /// <summary>Repositories no longer returned by the provider but kept because a Workspace still links them. Empty when nothing was kept.</summary>
    public IReadOnlyList<KeptRepositoryInfo> KeptMissingFromProvider { get; init; } = [];
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

/// <summary>
/// Describes a repository that is no longer returned by its provider but was kept because a Workspace links it.
/// Connector state never removes a Workspace's repository; only removing it from the Workspace does.
/// </summary>
public sealed class KeptRepositoryInfo
{
    public int RepositoryId { get; init; }
    public string RepositoryName { get; init; } = string.Empty;
    public string? OrgName { get; init; }
    /// <summary>Workspaces that link the repository, by name.</summary>
    public IReadOnlyList<string> WorkspaceNames { get; init; } = [];
    /// <summary>Features that use the repository, by name. Empty when none does.</summary>
    public IReadOnlyList<string> FeatureNames { get; init; } = [];
}
