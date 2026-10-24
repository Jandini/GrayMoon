using GrayMoon.App.Components.Pages;
using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Tests;

public class GitChangesSelectionPersistenceTests
{
    [Fact]
    public void Selection_round_trips_through_local_storage_json()
    {
        var selection = new WorkspaceGitChangesSelectionMemory.Selection(549, "src/App/Program.cs", true);

        var parsed = WorkspaceGitChanges.ParseSelection(WorkspaceGitChanges.SerializeSelection(selection));

        Assert.Equal(selection, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"workspaceRepositoryId\":1}")]
    [InlineData("{\"workspaceRepositoryId\":1,\"filePath\":\"\",\"isStagedSection\":false}")]
    public void Missing_or_malformed_stored_selection_parses_to_null(string? raw)
    {
        Assert.Null(WorkspaceGitChanges.ParseSelection(raw));
    }
}