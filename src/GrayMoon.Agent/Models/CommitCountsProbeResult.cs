namespace GrayMoon.Agent.Models;

/// <summary>
/// Result of counting commits for a branch. <paramref name="CountsProbed"/> separates "git answered
/// and there is genuinely nothing to report" from "the git command failed", so the app never has to
/// guess what a null count means.
/// </summary>
/// <param name="Outgoing">Commits on HEAD that are not on the comparison ref, or null when not computed.</param>
/// <param name="Incoming">Commits on the upstream ref that are not on HEAD, or null when the branch has no upstream.</param>
/// <param name="HasUpstream">Whether the branch has a configured upstream, from git config.</param>
/// <param name="CountsProbed">False when a git command failed, meaning the counts must not be persisted.</param>
/// <param name="UpstreamProbed">False when the upstream lookup was skipped, meaning <paramref name="HasUpstream"/> must not be persisted.</param>
public sealed record CommitCountsProbeResult(
    int? Outgoing,
    int? Incoming,
    bool HasUpstream,
    bool CountsProbed,
    bool UpstreamProbed)
{
    /// <summary>Nothing could be determined - neither the counts nor the upstream flag are usable.</summary>
    public static readonly CommitCountsProbeResult Unknown = new(null, null, false, false, false);
}
