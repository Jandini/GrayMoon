namespace GrayMoon.App.Services.GitChanges;

/// <summary>User's remembered "+ Push" preference for the workspace commit split button, tracked
/// separately for each automatic staged-only/all bucket (which bucket is active is never
/// user-selectable - it always auto-derives from whether any repository has staged changes). Missing
/// entries default to false, i.e. plain Commit All/Commit Staged with no push.</summary>
public sealed record WorkspaceGitChangesCommitModeSelection(bool PushWhenAll, bool PushWhenStaged);

/// <summary>
/// Circuit-scoped memory of the selected commit-button mode per workspace so SPA navigation away and
/// back can restore the split-button selection without localStorage or DB persistence. Mirrors
/// <see cref="WorkspaceGitChangesCommitMessageMemory"/>.
/// </summary>
public sealed class WorkspaceGitChangesCommitModeMemory
{
    private readonly Dictionary<int, WorkspaceGitChangesCommitModeSelection> _byWorkspace = new();

    public void Set(int workspaceId, WorkspaceGitChangesCommitModeSelection selection) =>
        _byWorkspace[workspaceId] = selection;

    public WorkspaceGitChangesCommitModeSelection? Get(int workspaceId) =>
        _byWorkspace.GetValueOrDefault(workspaceId);
}
