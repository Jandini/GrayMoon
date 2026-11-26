using GrayMoon.Application.Features;

namespace GrayMoon.App.Components.Features;

/// <summary>
/// The text of the Remove Feature dialog before any live check ran. It is built from stored facts only
/// (<see cref="RemoveFeatureSummary"/>), so it never calls a repository clean or safe: it says what the removal
/// will do, what it will keep, and what GrayMoon last recorded, named as that.
/// </summary>
internal static class RemoveFeatureQuickDialog
{
    internal const string SafetyText =
        "A folder with uncommitted changes, a lock or an unfinished merge is kept and listed in the result. Nothing is discarded.";

    internal const string BranchNote =
        "Without a review, a branch is deleted only when its commits are merged. Other branches are kept and listed in the result.";

    internal const string ReviewHint =
        "Review details checks every repository now and lets you choose to discard changes, unlock folders or delete branches that are not merged.";

    /// <summary>What will be removed, counted from the Feature's stored repositories.</summary>
    internal static string Headline(RemoveFeatureSummary summary)
    {
        var count = summary.RepositoryCount;
        var folders = count switch
        {
            0 => "Every folder of this Feature is already removed.",
            1 => "This removes the Feature's folder of 1 repository from your computer.",
            _ => $"This removes the Feature's folders of {count} repositories from your computer.",
        };
        return summary.AlreadyRemovedCount switch
        {
            0 => folders,
            _ when count == 0 => folders + " What is left is listed below.",
            1 => folders + " 1 repository was already removed.",
            _ => folders + $" {summary.AlreadyRemovedCount} repositories were already removed.",
        };
    }

    /// <summary>
    /// What GrayMoon last recorded about changes, said as a record and not as the present state. Null when nothing
    /// was recorded: no line is shown then, because "no recorded changes" is not "clean".
    /// </summary>
    internal static string? RecordedChangesText(RemoveFeatureSummary summary) => summary.RecordedChangedRepositoryCount switch
    {
        <= 0 => null,
        1 => "1 repository had uncommitted changes when GrayMoon last looked. It is kept unless you review the details and choose to discard them.",
        var n => $"{n} repositories had uncommitted changes when GrayMoon last looked. They are kept unless you review the details and choose to discard them.",
    };

    /// <summary>The local-branch choice is offered when any repository of the Feature has a Feature branch.</summary>
    internal static bool ShowDeleteLocalBranches(RemoveFeatureSummary summary) => summary.FeatureBranchCount > 0;

    /// <summary>The remote-branch choice is offered when GrayMoon recorded a push for a Feature branch.</summary>
    internal static bool ShowDeleteRemoteBranches(RemoveFeatureSummary summary) => summary.RecordedPushedBranchCount > 0;

    /// <summary>The line for one repository an earlier removal left unfinished.</summary>
    internal static string PendingText(RemoveFeaturePendingRepository pending)
        => string.IsNullOrWhiteSpace(pending.Message)
            ? pending.WorktreeRemoved ? "Folder removed; a branch was not deleted." : "The folder was kept."
            : pending.Message;
}
