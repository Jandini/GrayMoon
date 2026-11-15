using GrayMoon.App.Components.Pages;
using GrayMoon.App.Models;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceActionsLevelGroupingTests
{
    [Fact]
    public void Groups_are_the_Workspace_repository_then_levels_from_the_highest_down_then_no_level()
    {
        var rows = new[]
        {
            CreateRow("none", level: null),
            CreateRow("one", level: 1),
            CreateRow("workspace", level: 4, isWorkspaceRepository: true),
            CreateRow("three", level: 3),
            CreateRow("two", level: 2),
        };

        var groups = WorkspaceActions.BuildGroups(rows, groupByDependencyLevel: true);

        Assert.Equal(
            [
                (WorkspaceActions.ActionGroupKind.Workspace, (int?)null, "workspace"),
                (WorkspaceActions.ActionGroupKind.Level, 3, "three"),
                (WorkspaceActions.ActionGroupKind.Level, 2, "two"),
                (WorkspaceActions.ActionGroupKind.Level, 1, "one"),
                (WorkspaceActions.ActionGroupKind.Level, null, "none"),
            ],
            groups.Select(g => (g.Kind, g.LevelKey, Names(g))));
    }

    [Fact]
    public void The_Workspace_repository_is_never_in_a_level_group()
    {
        var rows = new[]
        {
            CreateRow("source", level: 2),
            CreateRow("workspace", level: 2, isWorkspaceRepository: true),
        };

        var groups = WorkspaceActions.BuildGroups(rows, groupByDependencyLevel: true);

        Assert.Equal(2, groups.Count);
        Assert.Equal("workspace", Names(groups[0]));
        Assert.Equal("source", Names(groups[1]));
    }

    [Fact]
    public void Without_a_dependency_graph_the_grid_is_one_flat_group_in_the_given_order()
    {
        var rows = new[]
        {
            CreateRow("b", level: 1),
            CreateRow("workspace", level: 3, isWorkspaceRepository: true),
            CreateRow("a", level: 2),
        };

        var group = Assert.Single(WorkspaceActions.BuildGroups(rows, groupByDependencyLevel: false));

        Assert.Equal(WorkspaceActions.ActionGroupKind.Flat, group.Kind);
        Assert.Equal("b,workspace,a", Names(group));
    }

    [Fact]
    public void Rows_keep_their_order_inside_a_group()
    {
        var rows = new[]
        {
            CreateRow("failed", level: 1),
            CreateRow("other-level", level: 2),
            CreateRow("running", level: 1),
            CreateRow("success", level: 1),
        };

        var groups = WorkspaceActions.BuildGroups(rows, groupByDependencyLevel: true);

        Assert.Equal("failed,running,success", Names(groups.Single(g => g.LevelKey == 1)));
    }

    [Fact]
    public void No_rows_give_no_groups()
    {
        Assert.Empty(WorkspaceActions.BuildGroups([], groupByDependencyLevel: true));
        Assert.Empty(WorkspaceActions.BuildGroups([], groupByDependencyLevel: false));
    }

    [Fact]
    public void A_Feature_uses_its_own_level_and_not_the_level_on_the_shared_link()
    {
        var sharedLink = new WorkspaceRepositoryLink { DependencyLevel = 5 };

        Assert.Equal(2, WorkspaceActions.ResolveDependencyLevel(sharedLink, isFeatureContext: true, contextDependencyLevel: 2));
        Assert.Null(WorkspaceActions.ResolveDependencyLevel(sharedLink, isFeatureContext: true, contextDependencyLevel: null));
        Assert.Equal(5, WorkspaceActions.ResolveDependencyLevel(sharedLink, isFeatureContext: false, contextDependencyLevel: 2));
    }

    private static string Names(WorkspaceActions.ActionRowGroup group) =>
        string.Join(",", group.Rows.Select(row => row.Repo.RepositoryName));

    private static WorkspaceActions.WorkspaceActionRow CreateRow(string repositoryName, int? level, bool isWorkspaceRepository = false) => new()
    {
        Link = new WorkspaceRepositoryLink { BranchName = "main" },
        Repo = new GitHubRepositoryEntry { RepositoryName = repositoryName, OrgName = "acme", ConnectorName = "GitHub" },
        DependencyLevel = level,
        IsWorkspaceRepository = isWorkspaceRepository
    };
}
