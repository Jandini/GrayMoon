namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// App-wide (singleton) memory of the in-progress commit message per Workspace and Feature context, typed or
/// AI-written. A Feature and the Workspace each keep their own text, so writing one does not replace the other.
/// Survives leaving Changes and a browser reload (F5 / Ctrl+F5 starts a new circuit). Kept until a commit
/// succeeds or the user empties the box; not persisted across App restarts. Thread-safe, since every circuit
/// and background commit job shares the one instance.
/// </summary>
public sealed class WorkspaceGitChangesCommitMessageMemory
{
    private readonly Dictionary<(int WorkspaceId, int ContextId), string> _byContext = new();
    private readonly object _gate = new();

    public void Set(int workspaceId, int contextId, string message)
    {
        lock (_gate)
        {
            var key = (workspaceId, contextId);
            if (string.IsNullOrEmpty(message))
            {
                _byContext.Remove(key);
                return;
            }

            _byContext[key] = message;
        }
    }

    public string Get(int workspaceId, int contextId)
    {
        lock (_gate)
            return _byContext.TryGetValue((workspaceId, contextId), out var message) ? message : string.Empty;
    }

    public void Clear(int workspaceId, int contextId)
    {
        lock (_gate)
            _byContext.Remove((workspaceId, contextId));
    }
}
