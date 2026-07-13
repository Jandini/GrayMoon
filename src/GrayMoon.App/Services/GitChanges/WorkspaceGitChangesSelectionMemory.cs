namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Circuit-scoped memory of the last selected Git Changes file per workspace so SPA navigation
/// away and back can restore tree selection (and re-fetch the diff) without localStorage or DB.
/// </summary>
public sealed class WorkspaceGitChangesSelectionMemory
{
    private readonly Dictionary<int, Selection> _byWorkspace = new();

    public sealed record Selection(int WorkspaceRepositoryId, string FilePath, bool IsStagedSection);

    public void Set(int workspaceId, Selection selection) => _byWorkspace[workspaceId] = selection;

    public bool TryGet(int workspaceId, out Selection selection) =>
        _byWorkspace.TryGetValue(workspaceId, out selection!);

    public void Clear(int workspaceId) => _byWorkspace.Remove(workspaceId);
}
