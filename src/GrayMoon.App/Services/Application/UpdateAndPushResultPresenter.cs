using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application;

namespace GrayMoon.App.Services.Application;

public enum UpdateAndPushNoticeKind
{
    /// <summary>Nothing to tell the user.</summary>
    None,
    Info,

    /// <summary>The run finished, but a follow-up step (package restore) failed.</summary>
    Warning,
    Error,

    /// <summary>Ask whether to continue with a normal push; <see cref="UpdateAndPushNotice.Approval"/> is the command to run on yes.</summary>
    Approval,
}

public sealed record UpdateAndPushNotice(
    UpdateAndPushNoticeKind Kind,
    string Message,
    ApproveNormalPushContinuation? Approval = null);

/// <summary>
/// The one place that turns a combined run's result into what the user is told, so the Repositories page and the
/// notification popup cannot drift apart. Each surface keeps only what is specific to it (the page also marks the grid).
/// </summary>
public static class UpdateAndPushResultPresenter
{
    /// <param name="errorsShownOnGrid">
    /// True on the Repositories page, where level and repository errors are already shown as callouts on the grid; the
    /// popup has no grid, so it states failures in a toast.
    /// </param>
    public static UpdateAndPushNotice Present(UpdateAndPushExecutionResult result, bool errorsShownOnGrid)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            // Done with a warning: the push landed, a package restore after it failed. Said on both surfaces; the
            // grid shows which repository, not that the run as a whole finished.
            UpdateAndPushStatus.Completed when PushOperationResult.DescribeWarnings(result.Push) is { } warning
                => new(UpdateAndPushNoticeKind.Warning, warning),
            UpdateAndPushStatus.Completed => new(UpdateAndPushNoticeKind.None, ""),
            UpdateAndPushStatus.NothingToPush => new(UpdateAndPushNoticeKind.Info, result.Summary),
            UpdateAndPushStatus.ApprovalRequired when result.PendingApprovalId is { Length: > 0 } id
                => new(UpdateAndPushNoticeKind.Approval, result.Summary, new ApproveNormalPushContinuation(result.WorkspaceId, result.ContextId, id)),
            UpdateAndPushStatus.UpdateFailed => errorsShownOnGrid
                ? new(UpdateAndPushNoticeKind.None, "")
                : new(UpdateAndPushNoticeKind.Error, result.Summary),
            UpdateAndPushStatus.PushFailed => errorsShownOnGrid
                ? new(UpdateAndPushNoticeKind.Info, result.Summary)
                : new(UpdateAndPushNoticeKind.Error, result.Summary),
            _ => new(UpdateAndPushNoticeKind.Error, result.Summary),
        };
    }
}
