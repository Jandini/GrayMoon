namespace GrayMoon.App.Services.Policy;

/// <summary>
/// The one home for a branch that has no upstream and no commits of its own (the gray cloud badge). Such a branch
/// is identical to where it started, so a push only publishes it and CI has never run on it.
/// </summary>
public static class UnpublishedBranchRules
{
    /// <summary>True when the branch has no upstream and nothing to push.</summary>
    public static bool HasNoUpstreamAndNoCommits(bool? branchHasUpstream, int? outgoingCommits) =>
        branchHasUpstream == false && !RepositoryOperationPolicyExpressions.HasCommitsToPush(outgoingCommits);

    /// <summary>
    /// The branch whose CI runs describe this checkout: the recorded parent branch while the branch has no upstream and
    /// no commits, else the checked-out branch. Without a recorded parent it stays on the checked-out branch.
    /// </summary>
    public static string? CiBranch(string? branchName, bool? branchHasUpstream, int? outgoingCommits, string? parentBranchName)
    {
        if (string.IsNullOrWhiteSpace(branchName) || !HasNoUpstreamAndNoCommits(branchHasUpstream, outgoingCommits))
            return branchName;
        var parent = StripOrigin(parentBranchName);
        return string.IsNullOrEmpty(parent) ? branchName : parent;
    }

    private static string? StripOrigin(string? branch)
    {
        var trimmed = branch?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) ? trimmed["origin/".Length..] : trimmed;
    }
}
