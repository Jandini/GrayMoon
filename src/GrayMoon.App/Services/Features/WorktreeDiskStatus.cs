namespace GrayMoon.App.Services.Features;

/// <summary>
/// Disk facts about one worktree, obtained from the Worker's InspectWorktree command rather than
/// App-side disk reads (the App never reads repository paths from local disk directly).
/// StatusUnknown is true when the Worker could not be reached or InspectWorktree failed; Exists,
/// IsDirty, HasUpstream, AheadOfUpstream and AheadOfDefault carry no meaning in that case and must
/// not be treated as Missing, clean, or zero commits.
/// </summary>
/// <param name="Branch">Live current branch short name from InspectWorktree; null when detached or unknown (09 SB-2, plan unit I1).</param>
/// <param name="FeatureBranchExists">True when the Feature branch requested from InspectWorktree exists; null when none was requested or the Worker could not confirm.</param>
/// <param name="FeatureBranchAheadOfDefault">Commits on the Feature branch not on the default branch, live from InspectWorktree; null when unknown.</param>
/// <param name="FeatureBranchHasUpstream">True when the Feature branch has a configured upstream; null when unknown.</param>
/// <param name="FeatureBranchAheadOfUpstream">Commits on the Feature branch not on its upstream; null when unknown.</param>
/// <param name="FeatureBranchSha">SHA of the Feature branch tip; null when missing or unknown (D4 lease delete).</param>
internal readonly record struct WorktreeDiskStatus(
    bool StatusUnknown,
    bool Exists,
    bool? IsDirty,
    bool? HasUpstream,
    int? AheadOfUpstream,
    int? AheadOfDefault,
    string? UnknownReason,
    bool IsLocked = false,
    string? LockReason = null,
    string? Branch = null,
    bool? FeatureBranchExists = null,
    int? FeatureBranchAheadOfDefault = null,
    bool? FeatureBranchHasUpstream = null,
    int? FeatureBranchAheadOfUpstream = null,
    string? FeatureBranchSha = null)
{
    public static WorktreeDiskStatus Unknown(string reason) => new(true, false, null, null, null, null, reason);

    public static WorktreeDiskStatus Known(
        bool exists, bool? isDirty, bool? hasUpstream, int? aheadOfUpstream, int? aheadOfDefault,
        bool isLocked = false, string? lockReason = null, string? branch = null,
        bool? featureBranchExists = null, int? featureBranchAheadOfDefault = null,
        bool? featureBranchHasUpstream = null, int? featureBranchAheadOfUpstream = null,
        string? featureBranchSha = null) =>
        new(false, exists, isDirty, hasUpstream, aheadOfUpstream, aheadOfDefault, null, isLocked, lockReason,
            branch, featureBranchExists, featureBranchAheadOfDefault, featureBranchHasUpstream, featureBranchAheadOfUpstream,
            featureBranchSha);
}
