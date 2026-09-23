using GrayMoon.Application;

namespace GrayMoon.Application.Features;

public interface IWorkspaceFeatureOperations
{
    Task<CreateFeatureResult> CreateFeatureAsync(
        int workspaceId,
        string featureName,
        WorkspaceFeatureBaseKindApplication baseKind,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RemoveFeaturePlan> AnalyzeRemoveFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RemoveFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        RemoveFeatureOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceFeatureContextInfo>> ListFeaturesAsync(
        int workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-repository parent/source branch captured at Feature creation (RepositoryId -> ParentBranchName).
    /// Empty for the special Workspace context. Values may be null when provenance was unknown.
    /// </summary>
    Task<IReadOnlyDictionary<int, string?>> GetParentBranchNamesByRepositoryIdAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default);
}

public enum WorkspaceFeatureBaseKindApplication
{
    CurrentWorkspace = 0
}

public sealed class CreateFeatureResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Condition { get; init; }
    public WorkspaceFeatureContextId? ContextId { get; init; }
    public int? WorkspaceFeatureId { get; init; }
}

public sealed class RemoveFeatureOptions
{
    public bool AllowDiscardUncommitted { get; init; }
    public bool AllowForceDeleteLocalBranches { get; init; }
    public bool DeleteRemoteBranches { get; init; }
}

public sealed class RemoveFeaturePlan
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public RemoveFeatureClassification Classification { get; init; }
    public IReadOnlyList<RemoveFeatureRepositoryPlan> Repositories { get; init; } = [];
    public bool IsAutomaticallySafe { get; init; }
    public string Summary { get; init; } = "";
}

public enum RemoveFeatureClassification
{
    Completed = 0,
    Abandoned = 1,
    Active = 2,
    NeedsRepair = 3
}

public sealed class RemoveFeatureRepositoryPlan
{
    public int WorkspaceRepositoryId { get; init; }
    public string RepositoryName { get; init; } = "";
    public string? WorktreePath { get; init; }
    public bool WorktreeExists { get; init; }
    public string? BranchName { get; init; }
    public string? HeadCommit { get; init; }
    public bool HasUncommittedChanges { get; init; }
    public bool HasStagedChanges { get; init; }
    public bool HasConflicts { get; init; }
    /// <summary>
    /// True when a live Feature-worktree status scan succeeded. Unknown or failed status is never
    /// treated as clean for automatic-safe classification.
    /// </summary>
    public bool LiveStatusEstablished { get; init; }
    public int? OutgoingCommits { get; init; }
    public bool HasUpstream { get; init; }
    public int? PullRequestNumber { get; init; }
    public string? PullRequestState { get; init; }
    public bool? PullRequestMerged { get; init; }
    public string? Warning { get; init; }
}
