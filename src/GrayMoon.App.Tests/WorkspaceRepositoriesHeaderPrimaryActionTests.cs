using GrayMoon.App.Components.Shared;

namespace GrayMoon.App.Tests;

/// <summary>
/// E2: the header's primary button always shows the next useful action, and offers "Remove" only when
/// the Feature's work is finished (at least one PR, all merged or closed, nothing left outside a PR).
/// These tests exercise the pure rule extracted from <see cref="WorkspaceRepositoriesHeader"/> directly.
/// </summary>
public sealed class WorkspaceRepositoriesHeaderPrimaryActionTests
{
    [Fact]
    public void FreshFeature_with_no_commits_and_no_pr_shows_Feature()
    {
        var action = WorkspaceRepositoriesHeader.DeterminePrimaryAction(
            isFeatureContext: true,
            hasCreatablePr: false,
            allFeaturePrsCompleted: false);

        Assert.Equal(WorkspaceRepositoriesHeader.HeaderPrimaryAction.Feature, action);
    }

    [Fact]
    public void Feature_with_creatable_pr_shows_CreatePr()
    {
        var action = WorkspaceRepositoriesHeader.DeterminePrimaryAction(
            isFeatureContext: true,
            hasCreatablePr: true,
            allFeaturePrsCompleted: false);

        Assert.Equal(WorkspaceRepositoriesHeader.HeaderPrimaryAction.CreatePr, action);
    }

    [Fact]
    public void Feature_with_open_pr_shows_Feature_not_Remove()
    {
        // Open PR means AllFeaturePrsCompleted is false (not every PR is merged or closed).
        var action = WorkspaceRepositoriesHeader.DeterminePrimaryAction(
            isFeatureContext: true,
            hasCreatablePr: false,
            allFeaturePrsCompleted: false);

        Assert.Equal(WorkspaceRepositoriesHeader.HeaderPrimaryAction.Feature, action);
    }

    [Fact]
    public void Feature_with_all_prs_merged_or_closed_shows_Remove()
    {
        var action = WorkspaceRepositoriesHeader.DeterminePrimaryAction(
            isFeatureContext: true,
            hasCreatablePr: false,
            allFeaturePrsCompleted: true);

        Assert.Equal(WorkspaceRepositoriesHeader.HeaderPrimaryAction.Remove, action);
    }

    [Fact]
    public void Feature_with_creatable_pr_never_shows_Remove_even_when_other_prs_are_completed()
    {
        // Create PR always wins over Remove: a repository with commits outside a PR still needs attention.
        var action = WorkspaceRepositoriesHeader.DeterminePrimaryAction(
            isFeatureContext: true,
            hasCreatablePr: true,
            allFeaturePrsCompleted: true);

        Assert.Equal(WorkspaceRepositoriesHeader.HeaderPrimaryAction.CreatePr, action);
    }

    // Regression guard R-E2: Workspace cases must match today exactly. AllFeaturePrsCompleted is never
    // consulted outside a Feature context, so the Workspace never shows "Remove".

    [Fact]
    public void Workspace_with_creatable_pr_shows_CreatePr()
    {
        var action = WorkspaceRepositoriesHeader.DeterminePrimaryAction(
            isFeatureContext: false,
            hasCreatablePr: true,
            allFeaturePrsCompleted: false);

        Assert.Equal(WorkspaceRepositoriesHeader.HeaderPrimaryAction.CreatePr, action);
    }

    [Fact]
    public void Workspace_otherwise_shows_Branch()
    {
        var action = WorkspaceRepositoriesHeader.DeterminePrimaryAction(
            isFeatureContext: false,
            hasCreatablePr: false,
            allFeaturePrsCompleted: false);

        Assert.Equal(WorkspaceRepositoriesHeader.HeaderPrimaryAction.Branch, action);
    }

    [Fact]
    public void Workspace_shows_Branch_even_if_allFeaturePrsCompleted_is_somehow_true()
    {
        var action = WorkspaceRepositoriesHeader.DeterminePrimaryAction(
            isFeatureContext: false,
            hasCreatablePr: false,
            allFeaturePrsCompleted: true);

        Assert.Equal(WorkspaceRepositoriesHeader.HeaderPrimaryAction.Branch, action);
    }
}
