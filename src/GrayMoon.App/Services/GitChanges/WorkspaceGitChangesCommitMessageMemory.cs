namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Circuit-scoped memory of the in-progress workspace commit message per workspace so SPA navigation
/// away and back can restore the textarea contents without localStorage or DB persistence. Mirrors
/// <see cref="WorkspaceGitChangesSelectionMemory"/>.
/// </summary>
public sealed class WorkspaceGitChangesCommitMessageMemory
{
    private readonly Dictionary<int, string> _byWorkspace = new();

    public void Set(int workspaceId, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            _byWorkspace.Remove(workspaceId);
            return;
        }

        _byWorkspace[workspaceId] = message;
    }

    public string Get(int workspaceId) =>
        _byWorkspace.TryGetValue(workspaceId, out var message) ? message : string.Empty;

    public void Clear(int workspaceId) => _byWorkspace.Remove(workspaceId);
}
