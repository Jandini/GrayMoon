using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Abstractions;

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
/// <c>Snapshot.Projects</c> in the agent's own model, kept so responses can continue to expose the
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

    /// <summary>Pre-resolved "origin/&lt;default&gt;" ref, so the probe does not resolve it again.</summary>
    public string? DefaultBranchOriginRef { get; init; }

    /// <summary>Branch to count against, when the caller already knows it (e.g. straight after checking it out).</summary>
    public string? BranchNameOverride { get; init; }

    /// <summary>Non-fatal error to carry on the snapshot, e.g. a fetch that failed before the probe ran.</summary>
    public string? ErrorMessage { get; init; }
}
