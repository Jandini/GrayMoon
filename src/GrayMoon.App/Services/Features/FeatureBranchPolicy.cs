namespace GrayMoon.App.Services.Features;

/// <summary>
/// Pure rules for a Feature's own branch (I2, 09 SB-1). No I/O.
/// </summary>
public static class FeatureBranchPolicy
{
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
}
