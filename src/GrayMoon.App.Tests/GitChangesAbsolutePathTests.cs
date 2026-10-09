using GrayMoon.App.Components.Pages;

namespace GrayMoon.App.Tests;

public class GitChangesAbsolutePathTests
{
    [Fact]
    public void Nested_repository_path_includes_the_repository_folder()
    {
        var path = WorkspaceGitChanges.BuildAbsoluteFilePath(
            @"C:\Work",
            "GrayMoon",
            "GrayMoon.Desktop",
            "GrayMoon",
            "src/App.xaml");

        Assert.Equal(@"C:\Work\GrayMoon\GrayMoon.Desktop\src\App.xaml", path);
    }

    [Fact]
    public void Workspace_repository_path_is_the_workspace_folder()
    {
        var path = WorkspaceGitChanges.BuildAbsoluteFilePath(
            @"C:\Work\",
            "GrayMoon",
            "GrayMoon",
            "graymoon",
            "CLAUDE.md");

        Assert.Equal(@"C:\Work\GrayMoon\CLAUDE.md", path);
    }

    [Fact]
    public void Feature_workspace_repository_path_is_the_feature_folder()
    {
        var path = WorkspaceGitChanges.BuildAbsoluteFilePath(
            @"C:\Work\GrayMoon\features",
            "my-feature",
            "Workspace-Repo",
            "Workspace-Repo",
            "src/File.cs");

        Assert.Equal(@"C:\Work\GrayMoon\features\my-feature\src\File.cs", path);
    }

    [Fact]
    public void Missing_workspace_repository_keeps_the_repository_folder()
    {
        var path = WorkspaceGitChanges.BuildAbsoluteFilePath(
            @"C:\Work",
            "GrayMoon",
            "Api",
            null,
            "src/File.cs");

        Assert.Equal(@"C:\Work\GrayMoon\Api\src\File.cs", path);
    }
}
