using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceRepositoryContextStates")]
public sealed class WorkspaceRepositoryContextState
{
    public int WorkspaceRepositoryContextStateId { get; set; }

    [Required]
    public int WorkspaceFeatureContextId { get; set; }

    [ForeignKey(nameof(WorkspaceFeatureContextId))]
    public WorkspaceFeatureContext? WorkspaceFeatureContext { get; set; }

    [Required]
    public int WorkspaceRepositoryId { get; set; }

    [ForeignKey(nameof(WorkspaceRepositoryId))]
    public WorkspaceRepositoryLink? WorkspaceRepository { get; set; }

    [MaxLength(200)]
    public string? BranchName { get; set; }

    [MaxLength(200)]
    public string? CheckedOutTag { get; set; }

    [MaxLength(64)]
    public string? HeadCommit { get; set; }

    public bool? HasNewerTag { get; set; }

    [MaxLength(100)]
    public string? GitVersion { get; set; }

    /// <summary>True while a Feature context row has no GitVersion computed for its own checkout yet (set by the Feature seed, cleared by the first GitVersion probe result). Null or false means the stored GitVersion, or its absence, is a probe result.</summary>
    public bool? GitVersionPending { get; set; }

    public int? Projects { get; set; }
    public int? OutgoingCommits { get; set; }
    public int? IncomingCommits { get; set; }
    public int? DefaultBranchBehindCommits { get; set; }
    public int? DefaultBranchAheadCommits { get; set; }
    public bool? BranchHasUpstream { get; set; }

    public RepoSyncStatus SyncStatus { get; set; } = RepoSyncStatus.NeedsSync;

    public int? DependencyLevel { get; set; }
    public int? Dependencies { get; set; }
    public int? UnmatchedDeps { get; set; }
    public int? OutOfDateFileLines { get; set; }
    public int? OutOfDateFileRepos { get; set; }
    public int? TotalFileConfigRepos { get; set; }
    public bool? HasSelfFileVersionToken { get; set; }
    public int? TotalFileLines { get; set; }
    public ProjectType? RepositoryType { get; set; }

    /// <summary>The version dependents and {@Repo} tokens use when <see cref="EffectiveVersionKind"/> is InheritedVerified; for Own, the calculated GitVersion it was resolved with. Written only by <c>WorkspaceRepositoryStateWriter.WriteEffectiveVersionAsync</c>.</summary>
    [MaxLength(100)]
    public string? EffectiveVersion { get; set; }

    /// <summary><see cref="Models.EffectiveVersionKind"/> as an int; null = never resolved.</summary>
    public int? EffectiveVersionKind { get; set; }

    /// <summary>The <see cref="HeadCommit"/> the effective version was resolved for. An inheritance counts only while HEAD is still this commit (I10).</summary>
    [MaxLength(64)]
    public string? EffectiveVersionCommitSha { get; set; }

    /// <summary>Why the kind was chosen (<see cref="EffectiveVersionReasonCodes"/>).</summary>
    [MaxLength(50)]
    public string? EffectiveVersionReasonCode { get; set; }

    /// <summary>
    /// What a branch point decision was made from, besides the commit: the parent branch, the branch point and the
    /// packages that had to be published, each with its registry. A stored decision is reused only while this is
    /// unchanged. Null for a decision that needed no branch point check, and for one stored before this column existed.
    /// </summary>
    [MaxLength(64)]
    public string? EffectiveVersionEvidenceKey { get; set; }
}
