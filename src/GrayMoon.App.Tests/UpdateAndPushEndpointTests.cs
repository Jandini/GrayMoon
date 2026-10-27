using GrayMoon.App.Api.Endpoints;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.AspNetCore.Http;

namespace GrayMoon.App.Tests;

/// <summary>
/// How the REST Update and Push (and Prepare Workspace's combined step) report the shared operation's status: success is
/// 200, a run that needs the caller's consent or was refused is 409 (never a silent normal push), a failure is 400.
/// </summary>
public sealed class UpdateAndPushEndpointTests
{
    private static UpdateAndPushExecutionResult Result(UpdateAndPushStatus status, string? approvalId = null)
        => new("run", 1, new WorkspaceFeatureContextId(1), status, "summary", PendingApprovalId: approvalId);

    [Theory]
    [InlineData(UpdateAndPushStatus.Completed, 200)]
    [InlineData(UpdateAndPushStatus.NothingToPush, 200)]
    [InlineData(UpdateAndPushStatus.ApprovalRequired, 409)]
    [InlineData(UpdateAndPushStatus.Rejected, 409)]
    [InlineData(UpdateAndPushStatus.UpdateFailed, 400)]
    [InlineData(UpdateAndPushStatus.PushFailed, 400)]
    public void Each_status_maps_to_its_http_code(UpdateAndPushStatus status, int expected)
    {
        var result = WorkspaceOperationsEndpoints.MapCombined(Result(status, status == UpdateAndPushStatus.ApprovalRequired ? "abc" : null));

        Assert.Equal(expected, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task A_missing_approval_id_is_a_bad_request_without_starting_anything()
    {
        var result = await WorkspaceOperationsEndpoints.ApproveUpdateAndPush(
            1, new ApproveUpdateAndPushApiRequest(), null!, null!, null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }
}
