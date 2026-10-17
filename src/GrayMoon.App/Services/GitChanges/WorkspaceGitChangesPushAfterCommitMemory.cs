namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// App-wide (singleton) memory of the "Push committed" checkbox per workspace, so a reload (F5 / Ctrl+F5)
/// and leaving Changes both restore it. Not persisted across App restarts. Missing entries default to unticked.
/// Thread-safe.
/// </summary>
public sealed class WorkspaceGitChangesPushAfterCommitMemory
{
    private readonly Dictionary<int, bool> _byWorkspace = new();
    private readonly object _gate = new();

    public void Set(int workspaceId, bool pushAfterCommit)
    {
        lock (_gate)
            _byWorkspace[workspaceId] = pushAfterCommit;
    }

    public bool Get(int workspaceId)
    {
        lock (_gate)
            return _byWorkspace.GetValueOrDefault(workspaceId, false);
    }
}
