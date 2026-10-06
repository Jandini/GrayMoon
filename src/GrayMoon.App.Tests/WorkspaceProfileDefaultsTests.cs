using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Services.Workspaces;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceProfileDefaultsTests
{
    [Fact]
    public void DotNet_type_defaults_to_GitVersion_and_GitHub_Actions()
    {
        var defaults = WorkspaceProfileDefaults.ForType(WorkspaceType.DotNetDependency);

        Assert.Equal(WorkspaceVersioningMode.GitVersion, defaults.Versioning);
        Assert.Equal(WorkspaceCiProvider.GitHubActions, defaults.Ci);
    }

    [Fact]
    public void Basic_type_defaults_to_no_versioning_and_no_CI()
    {
        var defaults = WorkspaceProfileDefaults.ForType(WorkspaceType.Basic);

        Assert.Equal(WorkspaceVersioningMode.None, defaults.Versioning);
        Assert.Equal(WorkspaceCiProvider.None, defaults.Ci);
    }
}
