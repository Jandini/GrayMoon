using GrayMoon.App.Api.Endpoints;
using GrayMoon.Application.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests.Policy.Characterization;

/// <summary>
/// Phase 0A: a REST write that cannot take the workspace lock is refused with 409 and never reaches its operation.
/// The helper that does this (<c>WorkspaceCommandHttp.RunExclusiveAsync</c>) is file-scoped, so it is exercised through
/// an endpoint that uses it; the operations are passed as null, which proves they are not touched.
/// </summary>
public sealed class OperationLockCharacterizationTests
{
    private const int WorkspaceId = 9;

    [Fact]
    public async Task Rest_write_is_refused_with_409_while_a_workspace_operation_runs()
    {
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(runner.TryStartStructural(
            WorkspaceId,
            "create-feature",
            WorkspaceJobKeys.RepositoriesOverlayKey(WorkspaceId),
            "Creating...",
            async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
            },
            out var running));
        await entered.Task;

        var result = await WorkspaceOperationsEndpoints.UpdateAndPush(WorkspaceId, null, null!, null!, runner, CancellationToken.None);

        Assert.Equal(409, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        release.SetResult();
        await running.WhenCompleted;
    }

    [Fact]
    public async Task Rest_write_is_refused_with_409_while_a_Feature_operation_runs_in_the_same_workspace()
    {
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(runner.TryStartContext(
            new WorkspaceFeatureContextId(5),
            WorkspaceId,
            "push",
            WorkspaceJobKeys.ContextOverlayKey(WorkspaceId, 5),
            "Push...",
            async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
            },
            out var running));
        await entered.Task;

        var result = await WorkspaceOperationsEndpoints.UpdateAndPush(WorkspaceId, null, null!, null!, runner, CancellationToken.None);

        Assert.Equal(409, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        release.SetResult();
        await running.WhenCompleted;
    }
}
