namespace GrayMoon.App.Components.Features;

/// <summary>
/// One Open-in click, for every tool.
/// The button disables when the click is accepted.
/// Posting the desktop command finishes the attempt on its own and does not wait for the toast.
/// The toast is feedback: once that attempt has finished and the toast is gone, the button enables.
/// </summary>
internal sealed class OpenInToolLaunch
{
    private bool _attemptFinished;
    private bool _feedbackEnded;

    private OpenInToolLaunch(string toolId) => ToolId = toolId;

    public string ToolId { get; }

    /// <summary>Id of the feedback toast, or 0 before it has been shown.</summary>
    public long FeedbackId { get; private set; }

    public bool AttemptFinished => _attemptFinished;

    /// <summary>
    /// Disabled from the click until the desktop post has been attempted and the feedback toast has ended.
    /// Ending the toast does not post the command. Finishing the post does not enable the button on its own.
    /// </summary>
    public bool IsButtonDisabled => !_attemptFinished || !_feedbackEnded;

    public static OpenInToolLaunch Begin(string toolId)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolId);
        return new OpenInToolLaunch(toolId);
    }

    public void NoteFeedback(long toastId)
    {
        if (toastId <= 0)
            throw new ArgumentOutOfRangeException(nameof(toastId));

        FeedbackId = toastId;
    }

    /// <summary>The desktop post has returned. This does not enable the button.</summary>
    public void FinishAttempt() => _attemptFinished = true;

    /// <summary>
    /// True only while the toast noted for this click is the one on screen.
    /// A show that has not been noted yet is not this click's toast.
    /// </summary>
    public bool IsFeedbackShowing(long shownId, bool toastVisible) =>
        FeedbackId != 0 && toastVisible && shownId == FeedbackId;

    /// <summary>The feedback toast is no longer showing. This does not post the command.</summary>
    public void EndFeedback() => _feedbackEnded = true;
}
