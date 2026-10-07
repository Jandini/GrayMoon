using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Tests;

/// <summary>
/// The Repositories page's header Push button. A Basic workspace once did nothing on click because the
/// dependency-aware flow's dialog state was never populated; these tests pin the routing so Basic starts the
/// push directly while .NET Dependency workspaces keep resolving dependencies (and the dialog) as before.
/// </summary>
public sealed class PushClickRouterTests
{
    private static WorkspaceCapabilities Caps(
        WorkspaceType type,
        WorkspaceVersioningMode versioning = WorkspaceVersioningMode.None,
        WorkspaceCiProvider ci = WorkspaceCiProvider.None)
        => new(type, versioning, ci);

    [Theory]
    [InlineData(WorkspaceVersioningMode.None, WorkspaceCiProvider.None)]
    [InlineData(WorkspaceVersioningMode.GitVersion, WorkspaceCiProvider.None)]
    [InlineData(WorkspaceVersioningMode.GitVersion, WorkspaceCiProvider.GitHubActions)]
    public void Basic_workspace_starts_the_push_directly_whatever_versioning_and_ci_are(
        WorkspaceVersioningMode versioning, WorkspaceCiProvider ci)
    {
        var route = PushClickRouter.ForCapabilities(Caps(WorkspaceType.Basic, versioning, ci));

        Assert.Equal(PushClickRoute.StartDirect, route);
    }

    [Fact]
    public void Capabilities_not_loaded_yet_starts_the_push_directly()
        => Assert.Equal(PushClickRoute.StartDirect, PushClickRouter.ForCapabilities(null));

    [Theory]
    [InlineData(WorkspaceVersioningMode.None, WorkspaceCiProvider.None)]
    [InlineData(WorkspaceVersioningMode.GitVersion, WorkspaceCiProvider.None)]
    [InlineData(WorkspaceVersioningMode.GitVersion, WorkspaceCiProvider.GitHubActions)]
    public void DotNetDependency_workspace_still_resolves_dependencies_first(
        WorkspaceVersioningMode versioning, WorkspaceCiProvider ci)
    {
        var route = PushClickRouter.ForCapabilities(Caps(WorkspaceType.DotNetDependency, versioning, ci));

        Assert.Equal(PushClickRoute.ResolveDependencies, route);
    }

    [Fact]
    public void Dependency_lookup_that_returns_nothing_reports_a_load_failure()
    {
        var route = PushClickRouter.ForDependencies(
            dependencyInfoLoaded: false, hasNoDependencies: false, anyDependencyNeedsPush: false);

        Assert.Equal(PushClickRoute.LoadFailed, route);
    }

    [Fact]
    public void No_dependencies_pushes_without_a_dialog_even_if_the_modal_check_would_pass()
    {
        var route = PushClickRouter.ForDependencies(
            dependencyInfoLoaded: true, hasNoDependencies: true, anyDependencyNeedsPush: true);

        Assert.Equal(PushClickRoute.StartDirect, route);
    }

    [Fact]
    public void Dependencies_that_have_nothing_to_push_skip_the_dialog()
    {
        var route = PushClickRouter.ForDependencies(
            dependencyInfoLoaded: true, hasNoDependencies: false, anyDependencyNeedsPush: false);

        Assert.Equal(PushClickRoute.StartDirect, route);
    }

    [Fact]
    public void A_dependency_with_commits_to_push_shows_the_dependency_dialog()
    {
        var route = PushClickRouter.ForDependencies(
            dependencyInfoLoaded: true, hasNoDependencies: false, anyDependencyNeedsPush: true);

        Assert.Equal(PushClickRoute.ShowDependencyModal, route);
    }
}
