using GrayMoon.App.Services.Application;
using GrayMoon.Application;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Tests;

public sealed class UpdateAndPushResultPresenterTests
{
    private static readonly WorkspaceFeatureContextId Context = new(7);

    private static UpdateAndPushExecutionResult Result(UpdateAndPushStatus status, string summary = "summary", string? approvalId = null)
        => new("run", 3, Context, status, summary, PendingApprovalId: approvalId);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Completed_says_nothing(bool onGrid)
        => Assert.Equal(UpdateAndPushNoticeKind.None, UpdateAndPushResultPresenter.Present(Result(UpdateAndPushStatus.Completed), onGrid).Kind);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Nothing_to_push_is_information(bool onGrid)
    {
        var notice = UpdateAndPushResultPresenter.Present(Result(UpdateAndPushStatus.NothingToPush, "Update complete. Nothing to push."), onGrid);

        Assert.Equal(UpdateAndPushNoticeKind.Info, notice.Kind);
        Assert.Equal("Update complete. Nothing to push.", notice.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Approval_carries_a_command_bound_to_the_workspace_context_and_approval(bool onGrid)
    {
        var notice = UpdateAndPushResultPresenter.Present(Result(UpdateAndPushStatus.ApprovalRequired, "Continue with normal push?", "abc"), onGrid);

        Assert.Equal(UpdateAndPushNoticeKind.Approval, notice.Kind);
        Assert.Equal(new ApproveNormalPushContinuation(3, Context, "abc"), notice.Approval);
    }

    [Fact]
    public void Approval_without_an_approval_id_is_an_error_not_a_prompt()
        => Assert.Equal(UpdateAndPushNoticeKind.Error, UpdateAndPushResultPresenter.Present(Result(UpdateAndPushStatus.ApprovalRequired), false).Kind);

    [Fact]
    public void Failures_are_left_to_the_grid_on_the_page_and_stated_in_a_toast_in_the_popup()
    {
        Assert.Equal(UpdateAndPushNoticeKind.None, UpdateAndPushResultPresenter.Present(Result(UpdateAndPushStatus.UpdateFailed), true).Kind);
        Assert.Equal(UpdateAndPushNoticeKind.Info, UpdateAndPushResultPresenter.Present(Result(UpdateAndPushStatus.PushFailed), true).Kind);
        Assert.Equal(UpdateAndPushNoticeKind.Error, UpdateAndPushResultPresenter.Present(Result(UpdateAndPushStatus.UpdateFailed, "Update stopped."), false).Kind);
        Assert.Equal(UpdateAndPushNoticeKind.Error, UpdateAndPushResultPresenter.Present(Result(UpdateAndPushStatus.PushFailed, "Level 2 failed."), false).Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rejected_is_always_an_error(bool onGrid)
    {
        var notice = UpdateAndPushResultPresenter.Present(Result(UpdateAndPushStatus.Rejected, "The Feature needs repair."), onGrid);

        Assert.Equal(UpdateAndPushNoticeKind.Error, notice.Kind);
        Assert.Equal("The Feature needs repair.", notice.Message);
    }
}
