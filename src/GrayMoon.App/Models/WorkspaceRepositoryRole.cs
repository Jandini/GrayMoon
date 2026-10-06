namespace GrayMoon.App.Models;

/// <summary>Role of a repository inside one Workspace. Persisted as int on WorkspaceRepositories.Role.</summary>
public enum WorkspaceRepositoryRole
{
    Source = 0,
    Workspace = 1
}
