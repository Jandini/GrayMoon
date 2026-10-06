using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Components.Pages;
using GrayMoon.App.Components.Shared;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Tests;

/// <summary>
/// Unit F: the Repositories grid and header are one page whose presentation is derived from the workspace
/// profile in one place (<see cref="WorkspaceGridPresentation"/>). There is no component test harness, so the
/// decisions are pure and tested directly: column set and colspan, slot layout (grouping), and which header
/// controls exist.
/// </summary>
public sealed class WorkspaceGridPresentationTests
{
    private static readonly WorkspaceCapabilities BasicNone =
        new(WorkspaceType.Basic, WorkspaceVersioningMode.None, WorkspaceCiProvider.None);

    private static readonly WorkspaceCapabilities BasicGitVersion =
        new(WorkspaceType.Basic, WorkspaceVersioningMode.GitVersion, WorkspaceCiProvider.None);

    private static readonly WorkspaceCapabilities DotNet = WorkspaceCapabilities.Legacy;

    // ---- column set, colspan and metrics ----

    [Fact]
    public void Basic_none_has_no_version_column_no_dependency_metric_and_no_grouping()
    {
        var p = WorkspaceGridPresentation.For(BasicNone);

        Assert.False(p.ShowVersionColumn);
        Assert.Equal(3, p.ColumnCount);
        Assert.False(p.ShowDependencyMetrics);
        Assert.Equal("metrics-grid--no-deps", p.MetricsGridModifierClass);
        Assert.False(p.GroupByDependencyLevel);
        Assert.False(p.ShowDependencyUpdateActions);
        Assert.False(p.ShowPackageRestore);
        Assert.False(p.ShowCiLinks);
    }

    [Fact]
    public void Basic_gitversion_adds_the_version_column_only()
    {
        var p = WorkspaceGridPresentation.For(BasicGitVersion);

        Assert.True(p.ShowVersionColumn);
        Assert.Equal(4, p.ColumnCount);
        Assert.False(p.ShowDependencyMetrics);
        Assert.False(p.GroupByDependencyLevel);
        Assert.False(p.ShowDependencyUpdateActions);
        Assert.False(p.ShowPackageRestore);
    }

    [Fact]
    public void DotNet_dependency_keeps_the_pre_profile_grid()
    {
        var p = WorkspaceGridPresentation.For(DotNet);

        Assert.True(p.ShowVersionColumn);
        Assert.Equal(4, p.ColumnCount); // the former TableColSpan const
        Assert.True(p.ShowDependencyMetrics);
        Assert.Equal(string.Empty, p.MetricsGridModifierClass);
        Assert.True(p.GroupByDependencyLevel);
        Assert.True(p.ShowDependencyUpdateActions);
        Assert.True(p.ShowPackageRestore);
        Assert.True(p.ShowCiLinks);
        Assert.False(p.ShowFileVersionUpdateAction);
        Assert.False(p.ShowBulkMergeInHeaderMenu);
    }

    [Fact]
    public void Unknown_capabilities_fall_back_to_the_pre_profile_grid()
    {
        Assert.Equal(WorkspaceGridPresentation.For(DotNet), WorkspaceGridPresentation.For(null));
        Assert.Equal(WorkspaceGridPresentation.For(DotNet), WorkspaceGridPresentation.Legacy);
    }

    [Theory]
    [InlineData(WorkspaceType.Basic, WorkspaceVersioningMode.None, 3)]
    [InlineData(WorkspaceType.Basic, WorkspaceVersioningMode.GitVersion, 4)]
    [InlineData(WorkspaceType.DotNetDependency, WorkspaceVersioningMode.None, 3)]
    [InlineData(WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion, 4)]
    public void Colspan_equals_the_rendered_column_count_for_every_combination(
        WorkspaceType type, WorkspaceVersioningMode versioning, int expectedColumns)
    {
        foreach (var ci in new[] { WorkspaceCiProvider.None, WorkspaceCiProvider.GitHubActions })
        {
            var p = WorkspaceGridPresentation.For(new WorkspaceCapabilities(type, versioning, ci));

            // Repository + Branch + metrics, plus Version when shown: must match the <th> set exactly.
            var renderedColumns = 3 + (p.ShowVersionColumn ? 1 : 0);
            Assert.Equal(expectedColumns, renderedColumns);
            Assert.Equal(renderedColumns, p.ColumnCount);
        }
    }

    [Fact]
    public void Ci_links_follow_the_ci_provider_not_the_workspace_type()
    {
        Assert.True(WorkspaceGridPresentation.For(
            new WorkspaceCapabilities(WorkspaceType.Basic, WorkspaceVersioningMode.None, WorkspaceCiProvider.GitHubActions)).ShowCiLinks);
        Assert.False(WorkspaceGridPresentation.For(
            new WorkspaceCapabilities(WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion, WorkspaceCiProvider.None)).ShowCiLinks);
    }

    // ---- slot layout (grouping and virtualization) ----

    [Fact]
    public void Flat_list_has_rows_only_and_no_synthetic_level_group()
    {
        var index = new[]
        {
            new WorkspaceRepositoryLinkIndexEntry(10, 100, null),
            new WorkspaceRepositoryLinkIndexEntry(11, 101, null),
            new WorkspaceRepositoryLinkIndexEntry(12, 102, null),
        };

        var slots = WorkspaceRepositories.ComputeSlots(index, groupByDependencyLevel: false);

        Assert.Equal(3, slots.Count);
        Assert.All(slots, s => Assert.Equal(WorkspaceRepositories.VirtualSlotKind.Row, s.Kind));
        Assert.Equal(new[] { 10, 11, 12 }, slots.Select(s => s.WorkspaceRepositoryId));
        Assert.Equal(new[] { 0, 1, 2 }, slots.Select(s => s.StripeIndex));
    }

    [Fact]
    public void Flat_list_ignores_stale_dependency_levels()
    {
        // Levels left behind by an earlier .NET profile must not regroup a Basic grid.
        var index = new[]
        {
            new WorkspaceRepositoryLinkIndexEntry(10, 100, 2),
            new WorkspaceRepositoryLinkIndexEntry(11, 101, 1),
            new WorkspaceRepositoryLinkIndexEntry(12, 102, null),
        };

        var slots = WorkspaceRepositories.ComputeSlots(index, groupByDependencyLevel: false);

        Assert.Equal(3, slots.Count);
        Assert.DoesNotContain(slots, s => s.Kind == WorkspaceRepositories.VirtualSlotKind.LevelHeader);
        Assert.All(slots, s => Assert.Null(s.LevelKey));
    }

    [Fact]
    public void Grouped_list_keeps_one_header_per_level_including_no_dependencies()
    {
        var index = new[]
        {
            new WorkspaceRepositoryLinkIndexEntry(10, 100, 2),
            new WorkspaceRepositoryLinkIndexEntry(11, 101, 2),
            new WorkspaceRepositoryLinkIndexEntry(12, 102, 1),
            new WorkspaceRepositoryLinkIndexEntry(13, 103, null),
        };

        var slots = WorkspaceRepositories.ComputeSlots(index, groupByDependencyLevel: true);

        var kinds = slots.Select(s => s.Kind).ToList();
        Assert.Equal(
            new[]
            {
                WorkspaceRepositories.VirtualSlotKind.LevelHeader, WorkspaceRepositories.VirtualSlotKind.Row, WorkspaceRepositories.VirtualSlotKind.Row,
                WorkspaceRepositories.VirtualSlotKind.LevelHeader, WorkspaceRepositories.VirtualSlotKind.Row,
                WorkspaceRepositories.VirtualSlotKind.LevelHeader, WorkspaceRepositories.VirtualSlotKind.Row,
            },
            kinds);
        var headers = slots.Where(s => s.Kind == WorkspaceRepositories.VirtualSlotKind.LevelHeader).ToList();
        Assert.Equal(new int?[] { 2, 1, null }, headers.Select(h => h.LevelKey));
        Assert.Equal(new[] { 2, 1, 1 }, headers.Select(h => h.LevelRepoCount));
        Assert.Equal(new[] { 0, 1, 2, 3 }, slots.Where(s => s.Kind == WorkspaceRepositories.VirtualSlotKind.Row).Select(s => s.StripeIndex));
    }

    [Fact]
    public void Empty_index_has_no_slots_either_way()
    {
        Assert.Empty(WorkspaceRepositories.ComputeSlots(Array.Empty<WorkspaceRepositoryLinkIndexEntry>(), groupByDependencyLevel: true));
        Assert.Empty(WorkspaceRepositories.ComputeSlots(Array.Empty<WorkspaceRepositoryLinkIndexEntry>(), groupByDependencyLevel: false));
    }

    // ---- header controls ----

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Basic_header_has_no_dependency_update_control_whatever_the_state(bool hasUnmatched, bool hasIncoming)
    {
        var p = WorkspaceGridPresentation.For(BasicNone);

        var control = WorkspaceRepositoriesHeader.DetermineUpdateControl(p.ShowDependencyUpdateActions, hasUnmatched, hasIncoming);

        Assert.Equal(WorkspaceRepositoriesHeader.HeaderUpdateControl.None, control);
        Assert.False(p.ShowPackageRestore);
    }

    [Theory]
    [InlineData(false, false, "Update")]
    [InlineData(true, false, "PushUpdated")]
    [InlineData(true, true, "Update")]
    [InlineData(false, true, "Update")]
    public void DotNet_header_update_control_matches_the_pre_profile_rule(
        bool hasUnmatched, bool hasIncoming, string expected)
    {
        var p = WorkspaceGridPresentation.For(DotNet);

        var control = WorkspaceRepositoriesHeader.DetermineUpdateControl(p.ShowDependencyUpdateActions, hasUnmatched, hasIncoming);

        Assert.Equal(expected, control.ToString());
        Assert.True(p.ShowPackageRestore);
    }

    [Fact]
    public void Basic_offers_file_version_update_only_while_a_file_is_out_of_date()
    {
        var p = WorkspaceGridPresentation.For(BasicNone);

        Assert.True(p.ShowFileVersionUpdateAction);
        Assert.True(WorkspaceRepositoriesHeader.DetermineShowsFileVersionUpdate(p.ShowFileVersionUpdateAction, hasOutOfDateFiles: true));
        Assert.False(WorkspaceRepositoriesHeader.DetermineShowsFileVersionUpdate(p.ShowFileVersionUpdateAction, hasOutOfDateFiles: false));
    }

    [Fact]
    public void DotNet_never_shows_the_standalone_file_version_button()
    {
        var p = WorkspaceGridPresentation.For(DotNet);

        Assert.False(WorkspaceRepositoriesHeader.DetermineShowsFileVersionUpdate(p.ShowFileVersionUpdateAction, hasOutOfDateFiles: true));
    }

    [Fact]
    public void Bulk_merge_moves_to_the_header_menu_only_for_a_flat_grid()
    {
        Assert.True(WorkspaceGridPresentation.For(BasicNone).ShowBulkMergeInHeaderMenu);
        Assert.True(WorkspaceGridPresentation.For(BasicGitVersion).ShowBulkMergeInHeaderMenu);
        Assert.False(WorkspaceGridPresentation.For(DotNet).ShowBulkMergeInHeaderMenu);
    }
}
