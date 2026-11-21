using GrayMoon.App.Services.Workspaces;

namespace GrayMoon.App.Tests;

public sealed class ExistingWorkspaceFolderTests
{
    [Fact]
    public void Add_can_use_a_folder_that_does_not_exist_or_is_empty()
    {
        Assert.Null(ExistingWorkspaceFolder.AddBlockedReason(new WorkspaceDirectoryState(false, null, null)));
        Assert.Null(ExistingWorkspaceFolder.AddBlockedReason(new WorkspaceDirectoryState(true, true, null)));
    }

    [Fact]
    public void Add_refuses_a_folder_with_content_and_points_to_import_from_disk()
    {
        var reason = ExistingWorkspaceFolder.AddBlockedReason(new WorkspaceDirectoryState(true, false, null));

        Assert.Equal(ExistingWorkspaceFolder.NotEmptyMessage, reason);
        Assert.Contains("Import from disk", reason);
    }

    [Fact]
    public void Add_refuses_a_folder_that_could_not_be_checked_instead_of_assuming_it_is_free()
    {
        var unknownEmptiness = ExistingWorkspaceFolder.AddBlockedReason(new WorkspaceDirectoryState(true, null, null));
        var workerError = ExistingWorkspaceFolder.AddBlockedReason(new WorkspaceDirectoryState(false, null, "The Worker timed out."));

        Assert.Equal(ExistingWorkspaceFolder.NotEmptyMessage, unknownEmptiness);
        Assert.Equal("Could not check the Workspace folder: The Worker timed out.", workerError);
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
