namespace GrayMoon.App.Services.GitChanges;

/// <summary>What a workspace commit run commits, decided when the commit starts and not when the button was clicked.</summary>
/// <param name="Targets">Repositories to commit, as they are now.</param>
/// <param name="LeftOut">Repositories that are not committed, each with the reason to report.</param>
/// <param name="AddedSinceClick">Names of the targets that were not part of the click (they got changes afterwards).</param>
public sealed record WorkspaceCommitTargetPlan(
    IReadOnlyList<WorkspaceGitChangesRepositoryView> Targets,
    IReadOnlyList<(WorkspaceGitChangesRepositoryView Repository, string Reason)> LeftOut,
    IReadOnlyList<string> AddedSinceClick);

/// <summary>
/// The click fixes the mode and what the user confirmed; the repositories are read again when the commit starts.
/// Between the two the user may answer the default-branch dialog and the AI may spend minutes on the message, and a
/// repository that got changes in that time used to be left uncommitted without a word.
/// </summary>
public static class WorkspaceCommitTargetPlanner
{
    public static bool IsOnDefaultBranch(WorkspaceGitChangesRepositoryView repository) =>
        !string.IsNullOrWhiteSpace(repository.DefaultBranchName)
        && string.Equals(repository.BranchName, repository.DefaultBranchName, StringComparison.Ordinal);

    /// <param name="mode">The mode of the click.</param>
    /// <param name="atClick">The targets of the click. Those on their default branch were confirmed by the user.</param>
    /// <param name="current">Every repository of the context as persisted now.</param>
    public static WorkspaceCommitTargetPlan Plan(
        WorkspaceCommitMode mode,
        IReadOnlyList<WorkspaceGitChangesRepositoryView> atClick,
        IReadOnlyList<WorkspaceGitChangesRepositoryView> current)
    {
        var clickIds = atClick.Select(r => r.WorkspaceRepositoryId).ToHashSet();
        var confirmedOnDefaultBranch = atClick.Where(IsOnDefaultBranch).Select(r => r.WorkspaceRepositoryId).ToHashSet();
        var currentIds = current.Select(r => r.WorkspaceRepositoryId).ToHashSet();

        var targets = new List<WorkspaceGitChangesRepositoryView>();
        var leftOut = new List<(WorkspaceGitChangesRepositoryView, string)>();
        var added = new List<string>();

        foreach (var repository in current)
        {
            // A target of the click stays one even when its persisted counts are zero now: the Worker decides, and
            // "nothing to commit" comes back as a skip.
            var wasClicked = clickIds.Contains(repository.WorkspaceRepositoryId);
            if (!wasClicked && !WorkspaceCommitModeHelper.IsRepositoryTarget(mode, repository.StagedCount, repository.ChangedCount))
                continue;

            // Never commit to a default branch the user did not confirm.
            if (IsOnDefaultBranch(repository) && !confirmedOnDefaultBranch.Contains(repository.WorkspaceRepositoryId))
            {
                leftOut.Add((repository, $"it is on its default branch ({repository.DefaultBranchName}) and that was not confirmed for this commit; commit again to include it"));
                continue;
            }

            targets.Add(repository);
            if (!wasClicked)
                added.Add(repository.RepositoryName);
        }

        foreach (var repository in atClick.Where(r => !currentIds.Contains(r.WorkspaceRepositoryId)))
            leftOut.Add((repository, "it is no longer part of this context"));

        return new WorkspaceCommitTargetPlan(targets, leftOut, added);
    }
}