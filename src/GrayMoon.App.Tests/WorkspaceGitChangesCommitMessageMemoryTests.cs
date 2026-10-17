using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceGitChangesCommitMessageMemoryTests
{
    [Fact]
    public void Set_then_Get_returns_the_message_for_that_workspace_and_context()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();

        memory.Set(1, 10, "wip: split parser");

        Assert.Equal("wip: split parser", memory.Get(1, 10));
        Assert.Equal(string.Empty, memory.Get(2, 10));
        Assert.Equal(string.Empty, memory.Get(1, 20));
    }

    [Fact]
    public void In_progress_message_survives_until_Clear()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();
        memory.Set(1, 10, "wip: split parser");

        Assert.Equal("wip: split parser", memory.Get(1, 10));

        memory.Clear(1, 10);

        Assert.Equal(string.Empty, memory.Get(1, 10));
    }

    [Fact]
    public void Set_empty_removes_the_stored_message()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();
        memory.Set(1, 10, "wip");
        memory.Set(1, 10, "");

        Assert.Equal(string.Empty, memory.Get(1, 10));
    }

    [Fact]
    public void Clear_does_not_affect_another_context_in_the_same_workspace()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();
        memory.Set(1, 10, "feature");
        memory.Set(1, 20, "workspace");

        memory.Clear(1, 10);

        Assert.Equal(string.Empty, memory.Get(1, 10));
        Assert.Equal("workspace", memory.Get(1, 20));
    }
}
