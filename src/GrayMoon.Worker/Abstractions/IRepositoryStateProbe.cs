using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Single definition of how a repository's git state is read. Every command that reports state
/// back to the app captures it through this probe so the branch, tag, commit-count and upstream
/// values can never drift apart between one command and another.
/// </summary>
public interface IRepositoryStateProbe
{
    /// <summary>
    /// Captures the current state of <paramref name="repoPath"/>. Only the groups requested are
    /// inspected; the returned snapshot's probe markers say exactly which ones were, so the app can
    /// apply replace semantics to those and leave the rest untouched.
    /// </summary>
    Task<RepositoryStateCapture> CaptureAsync(string repoPath, RepositoryStateProbeOptions options, CancellationToken ct = default);
}

/// <summary>
/// What the probe found. <paramref name="Projects"/> is the same project set as
/// <c>Snapshot.Projects</c> in the worker's own model, kept so responses can continue to expose the
/// pre-snapshot <c>projects</c> field for apps that predate <see cref="RepositoryStateSnapshot"/>.
/// </summary>
public sealed record RepositoryStateCapture(RepositoryStateSnapshot Snapshot, IReadOnlyList<CsProjFileInfo>? Projects);

/// <summary>Selects which groups <see cref="IRepositoryStateProbe.CaptureAsync"/> inspects.</summary>
public sealed class RepositoryStateProbeOptions
{
    /// <summary>Run GitVersion. Off by default because it is by far the most expensive call in the probe.</summary>
    public bool IncludeGitVersion { get; init; }

    /// <summary>Run GitVersion with /nonormalize, for flows that have already ensured fetch ordering.</summary>
    public bool GitVersionNonNormalize { get; init; }

    /// <summary>List local branches, remote branches and tags.</summary>
    public bool IncludeBranchLists { get; init; }

    /// <summary>List remote branches only, so the app can prune deleted ones without a full branch refresh.</summary>
    public bool IncludeRemoteBranchesOnly { get; init; }

    /// <summary>Scan the working tree for .csproj files.</summary>
    public bool IncludeProjects { get; init; }

    /// <summary>
    /// Count commits against the upstream and the divergence base. On by default, unlike the other groups,
    /// because almost every caller wants them. The pre-push hook is the exception: it runs before the push
    /// data is transferred, so counts read then are stale and must not be reported as probed.
    /// </summary>
    public bool IncludeCommitCounts { get; init; } = true;

    /// <summary>Pre-resolved "origin/&lt;default&gt;" ref, so the probe does not resolve it again.</summary>
    public string? DefaultBranchOriginRef { get; init; }

    /// <summary>
    /// Optional origin ref (or branch name) for ahead/behind divergence instead of the repository default.
    /// Feature contexts pass the parent/PR-base branch; Workspace leaves this null.
    /// </summary>
    public string? DivergenceBaseOriginRef { get; init; }

    /// <summary>Branch to count against, when the caller already knows it (e.g. straight after checking it out).</summary>
    public string? BranchNameOverride { get; init; }

    /// <summary>Non-fatal error to carry on the snapshot, e.g. a fetch that failed before the probe ran.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Which optional enrichment the workspace's profile activates. A group the capabilities switch off is
    /// skipped even when the <c>Include*</c> flag above asks for it, and its probe marker stays false. Null
    /// means "not stated" and keeps the pre-profile behaviour of honouring the flags as given.
    /// </summary>
    public RepositoryOperationCapabilities? Capabilities { get; init; }
}
