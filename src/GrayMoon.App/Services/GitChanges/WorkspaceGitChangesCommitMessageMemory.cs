namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// App-wide (singleton) memory of the in-progress workspace commit message per workspace, typed or
/// AI-written, so the textarea contents survive SPA navigation away and back and a browser reload
/// (F5 / Ctrl+F5 starts a new circuit, so circuit-scoped state would be lost). Kept until a commit
/// succeeds or the user empties the box; not persisted across App restarts. Thread-safe, since every
/// circuit and background commit job shares the one instance.
/// </summary>
public sealed class WorkspaceGitChangesCommitMessageMemory
{
    private readonly Dictionary<int, string> _byWorkspace = new();
    private readonly object _gate = new();

    public void Set(int workspaceId, string message)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(message))
            {
                _byWorkspace.Remove(workspaceId);
                return;
            }

            _byWorkspace[workspaceId] = message;
        }
    }

    public string Get(int workspaceId)
    {
        lock (_gate)
            return _byWorkspace.TryGetValue(workspaceId, out var message) ? message : string.Empty;
    }

    public void Clear(int workspaceId)
    {
        lock (_gate)
            _byWorkspace.Remove(workspaceId);
    }
}
