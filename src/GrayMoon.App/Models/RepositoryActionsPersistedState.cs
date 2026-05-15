namespace GrayMoon.App.Models;

/// <summary>CI action status loaded from persistence for one workspace-repository link (multiple workflows).</summary>
public sealed class RepositoryActionsPersistedState
{
    public string? BranchName { get; init; }

    public IReadOnlyList<ActionStatusInfo> Workflows { get; init; } = Array.Empty<ActionStatusInfo>();
}
