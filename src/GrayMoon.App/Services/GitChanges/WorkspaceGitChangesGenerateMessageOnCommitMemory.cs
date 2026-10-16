namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Circuit-scoped memory of the AI menu's "Generate commit message" option per workspace: when ticked (and a provider
/// is pinned), Commit with an empty message box writes the message with that provider first. Mirrors
/// <see cref="WorkspaceGitChangesPushAfterCommitMemory"/>, so it lives exactly as long as the "Push committed" choice
/// it runs with. Missing entries default to unticked.
/// </summary>
public sealed class WorkspaceGitChangesGenerateMessageOnCommitMemory
{
    private readonly Dictionary<int, bool> _byWorkspace = new();

    public void Set(int workspaceId, bool generateMessageOnCommit) => _byWorkspace[workspaceId] = generateMessageOnCommit;

    public bool Get(int workspaceId) => _byWorkspace.GetValueOrDefault(workspaceId, false);
}
