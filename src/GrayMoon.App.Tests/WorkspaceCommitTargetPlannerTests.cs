using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Tests;

/// <summary>
/// The commit targets used to be fixed at the click, before the default-branch dialog and the AI step (up to
/// 600 s). A repository that got changes in that window was not committed and nothing said so.
/// </summary>
public sealed class WorkspaceCommitTargetPlannerTests
{
    private static WorkspaceGitChangesRepositoryView Repo(int id, int staged = 0, int changed = 0, string branch = "feature/x", string? defaultBranch = "main") =>
        new()
        {
            WorkspaceRepositoryId = id,
            RepositoryId = id + 100,
            RepositoryName = "repo-" + id,
            BranchName = branch,
            DefaultBranchName = defaultBranch,
            StagedCount = staged,
            ChangedCount = changed,
            Changes = [],
        };

    [Fact]
    public void Commit_All_includes_a_repository_that_became_dirty_after_the_click_and_names_it()
    {
        var atClick = new[] { Repo(1, changed: 2) };
        var current = new[] { Repo(1, changed: 2), Repo(2, changed: 1), Repo(3) };

        var plan = WorkspaceCommitTargetPlanner.Plan(WorkspaceCommitMode.All, atClick, current);

        Assert.Equal([1, 2], plan.Targets.Select(t => t.WorkspaceRepositoryId));
        Assert.Equal(["repo-2"], plan.AddedSinceClick);
        Assert.Empty(plan.LeftOut);
    }

    [Fact]
    public void Commit_Staged_uses_the_current_staged_counts()
    {
        var atClick = new[] { Repo(1, staged: 1) };
        var current = new[] { Repo(1, staged: 1), Repo(2, staged: 3), Repo(3, changed: 4) };

        var plan = WorkspaceCommitTargetPlanner.Plan(WorkspaceCommitMode.Staged, atClick, current);

        Assert.Equal([1, 2], plan.Targets.Select(t => t.WorkspaceRepositoryId));
        Assert.Equal(["repo-2"], plan.AddedSinceClick);
    }

    [Fact]
    public void A_clicked_repository_whose_counts_are_zero_now_is_still_sent_so_the_Worker_decides()
    {
        var plan = WorkspaceCommitTargetPlanner.Plan(WorkspaceCommitMode.All, [Repo(1, changed: 2)], [Repo(1)]);

        Assert.Equal([1], plan.Targets.Select(t => t.WorkspaceRepositoryId));
        Assert.Empty(plan.AddedSinceClick);
    }

    [Fact]
    public void A_repository_on_its_default_branch_that_was_not_confirmed_is_left_out_with_the_reason()
    {
        var atClick = new[] { Repo(1, changed: 1, branch: "main") };
        var current = new[] { Repo(1, changed: 1, branch: "main"), Repo(2, changed: 1, branch: "main"), Repo(3, changed: 1) };

        var plan = WorkspaceCommitTargetPlanner.Plan(WorkspaceCommitMode.All, atClick, current);

        // 1 was confirmed in the dialog, 3 is on a feature branch, 2 would be a commit to main nobody agreed to.
        Assert.Equal([1, 3], plan.Targets.Select(t => t.WorkspaceRepositoryId));
        var (repository, reason) = Assert.Single(plan.LeftOut);
        Assert.Equal(2, repository.WorkspaceRepositoryId);
        Assert.Contains("default branch (main)", reason);
    }

    [Fact]
    public void A_clicked_repository_that_moved_to_its_default_branch_after_the_click_is_left_out()
    {
        var plan = WorkspaceCommitTargetPlanner.Plan(
            WorkspaceCommitMode.All, [Repo(1, changed: 1)], [Repo(1, changed: 1, branch: "main")]);

        Assert.Empty(plan.Targets);
        Assert.Equal(1, Assert.Single(plan.LeftOut).Repository.WorkspaceRepositoryId);
    }

    [Fact]
    public void A_clicked_repository_that_left_the_context_is_reported()
    {
        var plan = WorkspaceCommitTargetPlanner.Plan(WorkspaceCommitMode.All, [Repo(1, changed: 1), Repo(2, changed: 1)], [Repo(1, changed: 1)]);

        Assert.Equal([1], plan.Targets.Select(t => t.WorkspaceRepositoryId));
        var (repository, reason) = Assert.Single(plan.LeftOut);
        Assert.Equal(2, repository.WorkspaceRepositoryId);
        Assert.Contains("no longer part of this context", reason);
    }
}