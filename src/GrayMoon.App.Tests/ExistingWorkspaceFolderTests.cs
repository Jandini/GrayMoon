using GrayMoon.App.Services.Workspaces;

namespace GrayMoon.App.Tests;

public sealed class ExistingWorkspaceFolderTests
{
    [Fact]
    public void Callout_counts_child_repositories_when_the_folder_is_not_a_workspace_repository()
    {
        Assert.Equal(
            "The workspace directory exists with 1 repository.",
            ExistingWorkspaceFolder.Callout(isWorkspaceRepository: false, workspaceRepositoryImported: false, repositoryCount: 1));
        Assert.Equal(
            "The workspace directory exists with 2 repositories.",
            ExistingWorkspaceFolder.Callout(isWorkspaceRepository: false, workspaceRepositoryImported: false, repositoryCount: 2));
    }

    [Fact]
    public void Callout_says_the_folder_is_a_workspace_repository()
    {
        Assert.Equal(
            "The workspace directory exists and is a workspace repository.",
            ExistingWorkspaceFolder.Callout(isWorkspaceRepository: true, workspaceRepositoryImported: true, repositoryCount: 0));
        Assert.Equal(
            "The workspace directory exists and is a workspace repository, with 3 repositories.",
            ExistingWorkspaceFolder.Callout(isWorkspaceRepository: true, workspaceRepositoryImported: true, repositoryCount: 3));
    }

    [Fact]
    public void Callout_says_when_the_workspace_repository_is_not_imported()
    {
        Assert.Equal(
            "The workspace directory exists and is a workspace repository that is not imported yet.",
            ExistingWorkspaceFolder.Callout(isWorkspaceRepository: true, workspaceRepositoryImported: false, repositoryCount: 0));
        Assert.Equal(
            "The workspace directory exists and is a workspace repository that is not imported yet, with 1 repository.",
            ExistingWorkspaceFolder.Callout(isWorkspaceRepository: true, workspaceRepositoryImported: false, repositoryCount: 1));
    }

    [Fact]
    public void Match_ignores_protocol_and_git_suffix()
    {
        var catalog = new (int RepositoryId, string CloneUrl)[]
        {
            (4, "https://github.com/acme/workspace.git"),
            (9, "https://github.com/acme/api.git"),
        };

        Assert.Equal(4, ExistingWorkspaceFolder.MatchRepositoryId("git@github.com:acme/workspace.git", catalog));
        Assert.Equal(9, ExistingWorkspaceFolder.MatchRepositoryId("https://github.com/acme/api", catalog));
        Assert.Null(ExistingWorkspaceFolder.MatchRepositoryId("https://github.com/acme/missing.git", catalog));
        Assert.Null(ExistingWorkspaceFolder.MatchRepositoryId(null, catalog));
    }
}
