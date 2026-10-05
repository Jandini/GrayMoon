using GrayMoon.App.Components.Modals;
using GrayMoon.Application;

namespace GrayMoon.App.Tests;

public sealed class NewPullRequestTargetBranchTests
{
    [Fact]
    public void Workspace_defaults_to_repository_default_branch()
    {
        var candidates = new[] { "develop", "main", "release/3.0" };
        var selected = NewPullRequestTargetBranch.ResolveInitialBase(
            parentBranchName: null,
            defaultBranch: "main",
            headBranch: "feature/foo",
            candidates);

        Assert.Equal("main", selected);
    }

    [Fact]
    public void Feature_with_existing_parent_preselects_parent()
    {
        var candidates = new[] { "develop", "main" };
        var selected = NewPullRequestTargetBranch.ResolveInitialBase(
            parentBranchName: "develop",
            defaultBranch: "main",
            headBranch: "feature/foo",
            candidates);

        Assert.Equal("develop", selected);
    }

    [Fact]
    public void Feature_with_deleted_parent_falls_back_to_default()
    {
        var candidates = new[] { "main", "release/3.0" };
        var selected = NewPullRequestTargetBranch.ResolveInitialBase(
            parentBranchName: "old-develop",
            defaultBranch: "main",
            headBranch: "feature/foo",
            candidates);

        Assert.Equal("main", selected);
    }

    [Fact]
    public void Old_Feature_without_parent_falls_back_to_default()
    {
        var candidates = new[] { "main", "develop" };
        var selected = NewPullRequestTargetBranch.ResolveInitialBase(
            parentBranchName: null,
            defaultBranch: "main",
            headBranch: "feature/foo",
            candidates);

        Assert.Equal("main", selected);
    }

    [Fact]
    public void Multi_repository_parents_resolve_independently()
    {
        var remotesA = NewPullRequestTargetBranch.BuildBaseCandidates(new WorkspaceBranchesSnapshot
        {
            RemoteBranches = ["origin/develop", "origin/main"],
            DefaultBranch = "main",
        });
        var remotesB = NewPullRequestTargetBranch.BuildBaseCandidates(new WorkspaceBranchesSnapshot
        {
            RemoteBranches = ["origin/release/3.0", "origin/main"],
            DefaultBranch = "main",
        });
        var remotesC = NewPullRequestTargetBranch.BuildBaseCandidates(new WorkspaceBranchesSnapshot
        {
            RemoteBranches = ["origin/main"],
            DefaultBranch = "main",
        });

        Assert.Equal("develop", NewPullRequestTargetBranch.ResolveInitialBase("develop", "main", "feature/foo", remotesA));
        Assert.Equal("release/3.0", NewPullRequestTargetBranch.ResolveInitialBase("release/3.0", "main", "feature/foo", remotesB));
        Assert.Equal("main", NewPullRequestTargetBranch.ResolveInitialBase("old-topic", "main", "feature/foo", remotesC));
    }

    [Fact]
    public void BuildBaseCandidates_uses_remote_short_names_not_only_local()
    {
        var candidates = NewPullRequestTargetBranch.BuildBaseCandidates(new WorkspaceBranchesSnapshot
        {
            LocalBranches = ["feature/foo"],
            RemoteBranches = ["origin/develop", "origin/main", "origin/hotfix/x"],
            DefaultBranch = "main",
        });

        Assert.Contains("develop", candidates);
        Assert.Contains("main", candidates);
        Assert.Contains("hotfix/x", candidates);
        Assert.DoesNotContain("feature/foo", candidates);
        Assert.DoesNotContain("origin/main", candidates);
    }

    [Fact]
    public void Parent_equal_to_head_is_not_preselected()
    {
        var selected = NewPullRequestTargetBranch.ResolveInitialBase(
            parentBranchName: "feature/foo",
            defaultBranch: "main",
            headBranch: "feature/foo",
            candidates: ["feature/foo", "main"]);

        Assert.Equal("main", selected);
    }
}
