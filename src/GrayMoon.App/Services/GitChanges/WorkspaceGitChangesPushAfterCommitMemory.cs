namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Circuit-scoped memory of the "Push committed" checkbox state per workspace, so SPA navigation away
/// and back restores the checkbox without localStorage or DB persistence. Mirrors
/// <see cref="WorkspaceGitChangesCommitMessageMemory"/>. Missing entries default to unchecked.
/// </summary>
public sealed class WorkspaceGitChangesPushAfterCommitMemory
{
    private readonly Dictionary<int, bool> _byWorkspace = new();

    public void Set(int workspaceId, bool pushAfterCommit) => _byWorkspace[workspaceId] = pushAfterCommit;

    public bool Get(int workspaceId) => _byWorkspace.GetValueOrDefault(workspaceId);
}
