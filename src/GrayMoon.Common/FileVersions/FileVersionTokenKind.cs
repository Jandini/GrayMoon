namespace GrayMoon.Common.FileVersions;
/// <summary>What a file-version pattern token resolves to.</summary>
public enum FileVersionTokenKind
{
    /// <summary>Persisted GitVersion for the repository (<c>{@Repo}</c>).</summary>
    GitVersion = 0,
    /// <summary>Persisted current branch name (<c>{@Repo:branch}</c>).</summary>
    Branch = 1,
    /// <summary>Current local HEAD commit SHA (<c>{@Repo:commit}</c>).</summary>
    Commit = 2,
}
