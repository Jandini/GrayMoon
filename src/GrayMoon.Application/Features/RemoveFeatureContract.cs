namespace GrayMoon.Application.Features;

/// <summary>
/// The steps Remove Feature takes for one repository, as the <see cref="OperationStepOutcome.Step"/> keys of its
/// <see cref="OperationOutcome"/>. Each has its own outcome: a removed folder with a kept branch is two answers.
/// </summary>
public static class RemoveFeatureSteps
{
    /// <summary>The Feature's folder of this repository (its git worktree) is unregistered and deleted.</summary>
    public const string Worktree = "Worktree";

    /// <summary>The local Feature branch is deleted.</summary>
    public const string LocalBranch = "LocalBranch";

    /// <summary>The remote Feature branch is deleted, with a lease on its tip.</summary>
    public const string RemoteBranch = "RemoteBranch";

    /// <summary>Files that were still in use are left in the folder, which is marked for later deletion.</summary>
    public const string Residue = "Residue";

    /// <summary>The operation name in <see cref="OperationOutcome.Operation"/>.</summary>
    public const string OperationName = "RemoveFeature";
}

/// <summary>
/// What the Remove Feature dialog shows before anything is checked live: stored facts only, read from the database
/// with no Worker command and no pull request refresh. Everything here that describes a checkout is the state
/// GrayMoon last recorded and may be out of date; the removal does not rely on it (it removes without force, and
/// git and the Worker refuse what is not safe to remove).
/// </summary>
public sealed class RemoveFeatureSummary
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string FeatureName { get; init; } = "";

    /// <summary>Repositories whose folder is still to be removed.</summary>
    public int RepositoryCount { get; init; }

    /// <summary>Repositories an earlier, incomplete removal already removed.</summary>
    public int AlreadyRemovedCount { get; init; }

    /// <summary>Repositories that have a Feature branch (not tag-pinned, not a read only snapshot).</summary>
    public int FeatureBranchCount { get; init; }

    /// <summary>Of those, how many were pushed as far as GrayMoon last recorded. Decides whether the remote-branch choice is offered.</summary>
    public int RecordedPushedBranchCount { get; init; }

    /// <summary>Repositories with changes in the last recorded Git Changes scan. A hint for the reader, never a check.</summary>
    public int RecordedChangedRepositoryCount { get; init; }

    /// <summary>What an earlier, incomplete removal left to do, one entry per repository that still needs attention.</summary>
    public IReadOnlyList<RemoveFeaturePendingRepository> Pending { get; init; } = [];
}

/// <summary>One repository an incomplete removal left unfinished, with the reason it recorded.</summary>
public sealed record RemoveFeaturePendingRepository(
    int WorkspaceRepositoryId,
    string RepositoryName,
    bool WorktreeRemoved,
    string? ReasonCode,
    string? Message,
    string? RequiredAuthorization);
