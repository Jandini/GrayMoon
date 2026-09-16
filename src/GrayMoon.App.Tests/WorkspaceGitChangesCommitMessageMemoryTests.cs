using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceGitChangesCommitMessageMemoryTests
{
    [Fact]
    public void Set_then_Get_returns_the_message_for_that_workspace()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();

        memory.Set(1, "wip: split parser");

        Assert.Equal("wip: split parser", memory.Get(1));
        Assert.Equal(string.Empty, memory.Get(2));
    }

    [Fact]
    public void In_progress_message_survives_until_Clear()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();
        memory.Set(1, "wip: split parser");

        Assert.Equal("wip: split parser", memory.Get(1));

        memory.Clear(1);

        Assert.Equal(string.Empty, memory.Get(1));
    }

    [Fact]
    public void Set_empty_removes_the_stored_message()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();
        memory.Set(1, "wip");
        memory.Set(1, "");

        Assert.Equal(string.Empty, memory.Get(1));
    }

    [Fact]
    public void Clear_does_not_affect_other_workspaces()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();
        memory.Set(1, "one");
        memory.Set(2, "two");

        memory.Clear(1);

        Assert.Equal(string.Empty, memory.Get(1));
        Assert.Equal("two", memory.Get(2));
    }
}
