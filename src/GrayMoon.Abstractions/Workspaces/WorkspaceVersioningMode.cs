namespace GrayMoon.Abstractions.Workspaces;

/// <summary>
/// How repository versions are calculated for a workspace. Deliberately independent of
/// <see cref="WorkspaceType"/>: a Basic Git workspace may use GitVersion even when its repositories
/// contain no .NET projects, and version-file tokens such as <c>{@Repo}</c> only need this setting.
/// </summary>
public enum WorkspaceVersioningMode
{
    /// <summary>
    /// No repository versioning. Version is "not applicable" rather than unresolved, and neither
    /// GitVersion nor a dotnet tool manifest restore runs as part of sync.
    /// </summary>
    None = 0,

    /// <summary>Versions are calculated with GitVersion.</summary>
    GitVersion = 1
}
