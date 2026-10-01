namespace GrayMoon.App.Services.Features;

/// <summary>
/// Disk facts about one worktree, obtained from the Agent's InspectWorktree command rather than
/// App-side disk reads (the App never reads repository paths from local disk directly).
/// StatusUnknown is true when the Agent could not be reached or InspectWorktree failed; Exists,
/// IsDirty, HasUpstream, AheadOfUpstream and AheadOfDefault carry no meaning in that case and must
/// not be treated as Missing, clean, or zero commits.
/// </summary>
internal readonly record struct WorktreeDiskStatus(
    bool StatusUnknown,
    bool Exists,
    bool? IsDirty,
    bool? HasUpstream,
    int? AheadOfUpstream,
    int? AheadOfDefault,
    string? UnknownReason,
    bool IsLocked = false,
    string? LockReason = null)
{
    public static WorktreeDiskStatus Unknown(string reason) => new(true, false, null, null, null, null, reason);

    public static WorktreeDiskStatus Known(
        bool exists, bool? isDirty, bool? hasUpstream, int? aheadOfUpstream, int? aheadOfDefault,
        bool isLocked = false, string? lockReason = null) =>
        new(false, exists, isDirty, hasUpstream, aheadOfUpstream, aheadOfDefault, null, isLocked, lockReason);
}
