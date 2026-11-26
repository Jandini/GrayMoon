namespace GrayMoon.Worker.Models;

/// <summary>
/// What a branch delete did and what it saw. <paramref name="BranchSha"/> and <paramref name="HasUpstream"/> are
/// the local branch's tip and whether it has a configured upstream, read before the delete from the same listing
/// that decides whether the branch exists; they are set for a local delete whenever the branch was found, also
/// when it was then kept, so a caller can use them as evidence for its next step (a lease, a pull request check).
/// </summary>
/// <param name="FailureKind">One of <see cref="BranchDeleteFailureKinds"/> when <paramref name="Success"/> is false; null otherwise.</param>
/// <param name="AlreadyAbsent">A remote delete found the branch already gone on the remote; nothing was pushed.</param>
public sealed record BranchDeleteResult(
    bool Success,
    string? ErrorMessage,
    string? FailureKind = null,
    string? BranchSha = null,
    bool? HasUpstream = null,
    bool AlreadyAbsent = false);

/// <summary>Why a branch was not deleted, established from the repository and not from git's message text where git offers a way.</summary>
public static class BranchDeleteFailureKinds
{
    /// <summary>The local branch does not exist.</summary>
    public const string NotFound = "NotFound";

    /// <summary>The branch is the one checked out in the repository the delete ran in.</summary>
    public const string CheckedOut = "CheckedOut";

    /// <summary>The tip is contained neither in HEAD nor in the default origin branch, and force was not asked for.</summary>
    public const string NotFullyMerged = "NotFullyMerged";

    /// <summary>The local tip is not the commit the caller expected.</summary>
    public const string TipChanged = "TipChanged";

    /// <summary>The remote tip is not the commit the caller expected (the lease was refused).</summary>
    public const string LeaseRefused = "LeaseRefused";

    /// <summary>The branch is the repository default branch and is never deleted.</summary>
    public const string Protected = "Protected";

    /// <summary>Anything else; the message has the detail.</summary>
    public const string Failed = "Failed";
}
