namespace GrayMoon.App.Services.Features;

/// <summary>Branch-changing actions a Feature context restricts (I3).</summary>
public enum FeatureBranchAction
{
    /// <summary>Check out a branch or a tag.</summary>
    Checkout = 0,
    CreateBranch = 1,
    ReturnToDefault = 2
}

/// <summary>
/// Pure rules for a Feature's own branch (I2, 09 SB-1; I3, 09 SB-3/SB-4/SB-5). No I/O.
/// </summary>
public static class FeatureBranchPolicy
{
    public const string TagNotPinnedMessage =
        "Tags can be checked out only in repositories pinned to a tag. Use the Workspace to check out a tag.";

    public const string CreateBranchMessage =
        "A Feature keeps every repository on its Feature branch. To work on another branch, create another Feature or use the Workspace.";

    public const string ReturnToDefaultMessage = "Return to Default is a Workspace action.";

    public const string PinnedToTagMessage =
        "This repository is pinned to a tag in this Feature. Use the Workspace to check out a branch.";

    /// <summary>
    /// The branch a Feature's repository is expected to be on: the Feature's own name for a
    /// non-pinned repository, or null for a repository pinned to a tag (no branch is expected).
    /// </summary>
    public static string? ExpectedBranch(string featureName, string? pinnedTag)
        => pinnedTag is null ? featureName : null;

    /// <summary>
    /// True when a repository has drifted off its expected Feature branch: there is an expected
    /// branch and the current branch (ordinal comparison) differs from it, including a null
    /// <paramref name="currentBranch"/> (detached HEAD).
    /// </summary>
    public static bool IsOffFeatureBranch(string? expectedBranch, string? currentBranch)
        => expectedBranch is not null && !string.Equals(expectedBranch, currentBranch, StringComparison.Ordinal);

    /// <summary>
    /// The Feature branch a grid row has drifted away from, or null when it has not (I4). Always null
    /// outside a Feature (<paramref name="featureName"/> null) and for a repository pinned to a tag.
    /// A blank <paramref name="currentBranch"/> counts as drift only when the context already has a
    /// recorded state for the repository (<paramref name="hasRecordedState"/>); before the first sync a
    /// blank branch means "unknown", not "detached".
    /// </summary>
    public static string? GetOffFeatureBranch(string? featureName, string? pinnedTag, string? currentBranch, bool hasRecordedState)
    {
        if (featureName is null)
            return null;
        if (string.IsNullOrEmpty(currentBranch) && !hasRecordedState)
            return null;
        var expected = ExpectedBranch(featureName, pinnedTag);
        return IsOffFeatureBranch(expected, string.IsNullOrEmpty(currentBranch) ? null : currentBranch) ? expected : null;
    }

    /// <summary>
    /// Decides whether <paramref name="action"/> is allowed in a Feature context. Returns null when it is
    /// allowed, otherwise the message to show the user. Fetch, delete, set upstream and Update Branch from
    /// Default are always allowed and are not routed through here.
    /// </summary>
    public static string? Evaluate(
        FeatureBranchAction action,
        string? expectedBranch,
        string? pinnedTag,
        string? target,
        bool isTag)
    {
        switch (action)
        {
            case FeatureBranchAction.CreateBranch:
                return CreateBranchMessage;
            case FeatureBranchAction.ReturnToDefault:
                return ReturnToDefaultMessage;
            case FeatureBranchAction.Checkout:
                if (isTag)
                    return pinnedTag is not null ? null : TagNotPinnedMessage;
                if (expectedBranch is null)
                    return PinnedToTagMessage;
                return string.Equals(expectedBranch, target, StringComparison.Ordinal)
                    ? null
                    : $"A Feature keeps every repository on its Feature branch '{expectedBranch}'. Use the Workspace to check out another branch.";
            default:
                return null;
        }
    }
}
