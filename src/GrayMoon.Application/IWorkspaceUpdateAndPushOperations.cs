using GrayMoon.Application.Features;

namespace GrayMoon.Application;

/// <summary>
/// The one supported way to start a combined Update and Push. Every UI surface (Repositories page, notification popup,
/// Level Only) submits an immutable command and presents the returned result; none of them chains the update and the
/// push itself. Update Only and Push Only stay separate operations.
/// </summary>
public interface IWorkspaceUpdateAndPushOperations
{
    /// <summary>
    /// Runs the combined operation: the two-lane pipeline first and, when the pipeline reports that it could not start
    /// (and so changed nothing), the sequential update followed by a synchronized push. A returned failure of any step
    /// stops the run; a normal (non synchronized) push only ever runs after <see cref="ApproveNormalPushContinuation"/>.
    /// Cancellation and unexpected exceptions propagate, after the failure was recorded in the operation outcome.
    /// </summary>
    Task<UpdateAndPushExecutionResult> ExecuteAsync(
        UpdateAndPushCommand command,
        UpdateAndPushObserver? observer,
        CancellationToken cancellationToken);

    /// <summary>
    /// Read-only: the repositories (in the command's context, up to its level) that will receive dependency commits
    /// while sitting on their default branch. Nothing is changed. The caller shows its warning from this list.
    /// </summary>
    Task<IReadOnlyList<UpdateAndPushDefaultBranchTarget>> GetDefaultBranchTargetsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int? maxLevel,
        CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="GetDefaultBranchTargetsAsync"/>, and also returns every in-scope repository that sits on its default
    /// branch (with or without updates). Pass that set as <see cref="StartUpdateAndPush.ReviewedDefaultBranchRepoIds"/>.
    /// </summary>
    Task<UpdateAndPushDefaultBranchReview> GetDefaultBranchReviewAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int? maxLevel,
        CancellationToken cancellationToken);
}

/// <summary>A repository that an Update and Push would commit to on its default (protected) branch.</summary>
public sealed record UpdateAndPushDefaultBranchTarget(int RepositoryId, string RepositoryName, string DefaultBranchName);

/// <summary>What the user is warned about, and the baseline the run later checks against.</summary>
public sealed record UpdateAndPushDefaultBranchReview(
    IReadOnlyList<UpdateAndPushDefaultBranchTarget> Targets,
    IReadOnlySet<int> OnDefaultBranchRepoIds);

/// <summary>Live callbacks for a run. All optional; the returned result is the complete account either way.</summary>
public sealed record UpdateAndPushObserver(
    Action<string>? ReportProgress = null,
    Action<int, string>? SetRepositoryError = null,
    Action<int, string>? SetLevelError = null);

/// <summary>
/// A request to the combined operation. <see cref="ContextId"/> is captured once by the caller when the user starts
/// the action and is used by every phase; it is never looked up again.
/// </summary>
public abstract record UpdateAndPushCommand(int WorkspaceId, WorkspaceFeatureContextId ContextId);

/// <summary>
/// Starts a combined run. <see cref="MaxLevel"/> null means every dependency level; a number means the levels at or
/// below it. There is no repository subset: the update walks the levels live. Options are explicit; the caller chooses
/// them (Repositories modal and notification popup: restore off; Prepare Workspace: restore on). <see cref="ReviewedDefaultBranchRepoIds"/> is the set the
/// user was shown when warned about default branches; when given, the run is refused if another in-scope repository has
/// moved onto its default branch since (one cheap read of the stored branch state, no Worker call). Null skips the check.
/// </summary>
public sealed record StartUpdateAndPush(
    int WorkspaceId,
    WorkspaceFeatureContextId ContextId,
    int? MaxLevel,
    string? CommitMessage,
    bool IncludeDepsInCommitMessage,
    bool RestorePackages,
    string? Origin = null,
    IReadOnlySet<int>? ReviewedDefaultBranchRepoIds = null) : UpdateAndPushCommand(WorkspaceId, ContextId);

/// <summary>
/// Continues a run that returned <see cref="UpdateAndPushStatus.ApprovalRequired"/> after the user explicitly agreed to a
/// normal push. The update is not run again. The approval is single use and bound to the Workspace and context.
/// </summary>
public sealed record ApproveNormalPushContinuation(
    int WorkspaceId,
    WorkspaceFeatureContextId ContextId,
    string PendingApprovalId) : UpdateAndPushCommand(WorkspaceId, ContextId);

public enum UpdateAndPushStatus
{
    /// <summary>Everything ran and every step succeeded.</summary>
    Completed,

    /// <summary>The update succeeded (or had nothing to do) and there was nothing to push.</summary>
    NothingToPush,

    /// <summary>The update reported errors. With the pipeline, levels that were already handed to the push lane may have been pushed.</summary>
    UpdateFailed,

    /// <summary>A push level or repository failed, or a package wait stopped the push.</summary>
    PushFailed,

    /// <summary>Synchronized push is not possible. Nothing was pushed after the update; a normal push needs the user's approval.</summary>
    ApprovalRequired,

    /// <summary>The request was refused before anything changed (unknown or unusable context, stale approval).</summary>
    Rejected,
}

/// <summary>
/// The outcome of one combined run. <see cref="Success"/> is derived from the whole run. <see cref="Update"/> and
/// <see cref="Push"/> carry the underlying results, including level errors, repository errors and package diagnostics.
/// </summary>
public sealed record UpdateAndPushExecutionResult(
    string RunId,
    int WorkspaceId,
    WorkspaceFeatureContextId ContextId,
    UpdateAndPushStatus Status,
    string Summary,
    bool Pipelined = false,
    DependencyUpdateRunResult? Update = null,
    OperationResult? Push = null,
    int PushedRepoCount = 0,
    string? PendingApprovalId = null)
{
    public bool Success => Status is UpdateAndPushStatus.Completed or UpdateAndPushStatus.NothingToPush;

    public bool NeedsApproval => Status == UpdateAndPushStatus.ApprovalRequired;
}
