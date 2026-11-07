using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public sealed class WorkerCommandPoolRouterTests
{
    [Theory]
    [InlineData("CollectGitEvidence", WorkerCommandPool.Ai)]
    [InlineData("GenerateAiText", WorkerCommandPool.Ai)]
    [InlineData("GetAiProviders", WorkerCommandPool.Ai)]
    [InlineData("GetGitChangeStatus", WorkerCommandPool.Read)]
    [InlineData("EnsureGitChangesWatchers", WorkerCommandPool.Read)]
    [InlineData("ListGitWorktrees", WorkerCommandPool.Read)]
    [InlineData("InspectWorktree", WorkerCommandPool.Read)]
    [InlineData("GetGitFileDiff", WorkerCommandPool.Diff)]
    [InlineData("SyncRepository", WorkerCommandPool.Main)]
    [InlineData("StageGitChanges", WorkerCommandPool.Main)]
    [InlineData("CommitGitChanges", WorkerCommandPool.Main)]
    [InlineData("StageAndCommit", WorkerCommandPool.Main)]
    [InlineData("GetHostInfo", WorkerCommandPool.Main)]
    [InlineData("SelfUpdate", WorkerCommandPool.Main)]
    [InlineData("SomethingNew", WorkerCommandPool.Main)]
    public void Commands_are_routed_to_their_pool(string command, WorkerCommandPool expected) =>
        Assert.Equal(expected, WorkerCommandPoolRouter.Resolve(command));
}
