namespace GrayMoon.App.Services.GitChanges;

/// <summary>What "Undo" across all repositories discards.</summary>
/// <param name="Targets">Repositories whose unstaged changes are discarded.</param>
/// <param name="LeftOut">Repositories with changes that are read only or on a tag; they are left as they are and named.</param>
public sealed record WorkspaceDiscardTargetPlan(
    IReadOnlyList<WorkspaceGitChangesRepositoryView> Targets,
    IReadOnlyList<WorkspaceGitChangesRepositoryView> LeftOut);

/// <summary>
/// Undo across all repositories never touches a repository that is read only or on a tag. Undo on one named repository, folder or file
/// is the user's own act and stays possible there; it does not go through this planner.
/// </summary>
public static class WorkspaceDiscardTargetPlanner
{
    public static WorkspaceDiscardTargetPlan PlanAll(IEnumerable<WorkspaceGitChangesRepositoryView> repositories)
    {
        var changed = repositories.Where(repository => repository.ChangedCount > 0).ToList();
        var leftOut = changed.Where(repository => repository.IsLocked).ToList();
        return new WorkspaceDiscardTargetPlan([.. changed.Except(leftOut)], leftOut);
    }
}
