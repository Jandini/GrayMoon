using GrayMoon.App.Services.Ui;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceRepositoryDisplayNameTests
{
    [Theory]
    [InlineData("GrayMoon.Workspace", "GrayMoon")]
    [InlineData("GrayMoon.workspace", "GrayMoon")]
    [InlineData("GrayMoon.WORKSPACE", "GrayMoon")]
    [InlineData("graymoon-workspace", "graymoon")]
    [InlineData("GrayMoon-Workspace", "GrayMoon")]
    [InlineData("Foo.Workspace.Workspace", "Foo.Workspace")]
    [InlineData("Api", "Api")]
    [InlineData("Workspace", "Workspace")]
    [InlineData(".Workspace", ".Workspace")]
    [InlineData("-workspace", "-workspace")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void ForWorkspaceRole_drops_a_trailing_workspace_suffix(string? repositoryName, string expected)
    {
        Assert.Equal(expected, WorkspaceRepositoryDisplayName.ForWorkspaceRole(repositoryName));
    }
}
