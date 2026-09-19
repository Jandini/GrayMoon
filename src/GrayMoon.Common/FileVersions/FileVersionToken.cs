namespace GrayMoon.Common.FileVersions;
/// <summary>
/// A parsed file-version pattern token. The configured syntax is <c>{@Repo}</c>,
/// <c>{@Repo:branch}</c>, or <c>{@Repo:commit}</c> (leading <c>@</c> optional for legacy configs).
/// </summary>
public sealed record FileVersionToken(string RepositoryName, FileVersionTokenKind Kind)
{
    /// <summary>
    /// Canonical key used when resolving and looking up values, e.g. <c>@Repo</c>,
    /// <c>@Repo:branch</c>, <c>@Repo:commit</c>.
    /// </summary>
    public string TokenKey => Kind switch
    {
        FileVersionTokenKind.Branch => $"@{RepositoryName}:branch",
        FileVersionTokenKind.Commit => $"@{RepositoryName}:commit",
        _ => $"@{RepositoryName}",
    };
}
