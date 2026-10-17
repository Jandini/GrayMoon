namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// App-wide (singleton) memory of the "Generate commit message" option per workspace. When ticked (and a provider
/// is pinned), Commit with an empty message box writes the message with that provider first. Lives as long as
/// <see cref="WorkspaceGitChangesPushAfterCommitMemory"/>, including across F5 / Ctrl+F5, and not across an App
/// restart. Missing entries default to ticked, until the user changes it for that workspace. Thread-safe.
/// </summary>
public sealed class WorkspaceGitChangesGenerateMessageOnCommitMemory
{
    private readonly Dictionary<int, bool> _byWorkspace = new();
    private readonly object _gate = new();

    public void Set(int workspaceId, bool generateMessageOnCommit)
    {
        lock (_gate)
            _byWorkspace[workspaceId] = generateMessageOnCommit;
    }

    public bool Get(int workspaceId)
    {
        lock (_gate)
            return _byWorkspace.GetValueOrDefault(workspaceId, true);
    }
}
