using GrayMoon.App.Components.Pages;

namespace GrayMoon.App.Tests;

/// <summary>
/// The grid's Version cell is three-valued. There is no component test harness in this repository, so the
/// decision is a pure static method on <see cref="WorkspaceRepositoriesRow"/> and these exercise it directly,
/// following <c>WorkspaceRepositoriesHeader.DeterminePrimaryAction</c>.
/// </summary>
public sealed class WorkspaceRepositoriesRowVersionCellTests
{
    [Fact]
    public void Resolved_version_is_shown_whatever_the_workspace_profile_says()
    {
        Assert.Equal(
            WorkspaceRepositoriesRow.VersionCell.Resolved,
            WorkspaceRepositoriesRow.DetermineVersionCell("1.2.3", isVersionUnresolved: false, workspaceUsesRepositoryVersioning: true));

        Assert.Equal(
            WorkspaceRepositoriesRow.VersionCell.Resolved,
            WorkspaceRepositoriesRow.DetermineVersionCell("1.2.3", isVersionUnresolved: false, workspaceUsesRepositoryVersioning: false));
    }

    [Fact]
    public void Missing_version_in_a_versioning_workspace_is_a_provider_failure()
    {
        // IsVersionUnresolved: synced (it has a branch or a tag) yet no version.
        Assert.Equal(
            WorkspaceRepositoriesRow.VersionCell.ProviderFailed,
            WorkspaceRepositoriesRow.DetermineVersionCell(null, isVersionUnresolved: true, workspaceUsesRepositoryVersioning: true));
    }

    [Fact]
    public void Missing_version_in_a_workspace_that_does_not_version_is_not_applicable()
    {
        Assert.Equal(
            WorkspaceRepositoriesRow.VersionCell.NotApplicable,
            WorkspaceRepositoriesRow.DetermineVersionCell(null, isVersionUnresolved: true, workspaceUsesRepositoryVersioning: false));
    }

    [Fact]
    public void Repository_that_has_never_synced_is_not_applicable_either_way()
    {
        Assert.Equal(
            WorkspaceRepositoriesRow.VersionCell.NotApplicable,
            WorkspaceRepositoriesRow.DetermineVersionCell(null, isVersionUnresolved: false, workspaceUsesRepositoryVersioning: true));

        Assert.Equal(
            WorkspaceRepositoriesRow.VersionCell.NotApplicable,
            WorkspaceRepositoriesRow.DetermineVersionCell(null, isVersionUnresolved: false, workspaceUsesRepositoryVersioning: false));
    }

    [Fact]
    public void Empty_version_string_is_treated_as_no_version()
    {
        Assert.Equal(
            WorkspaceRepositoriesRow.VersionCell.ProviderFailed,
            WorkspaceRepositoriesRow.DetermineVersionCell("", isVersionUnresolved: true, workspaceUsesRepositoryVersioning: true));
    }
}
