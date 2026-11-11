using GrayMoon.Application.Features;

namespace GrayMoon.Application.Repositories.Policy;

/// <summary>Which row a repository's checkout state is read from.</summary>
public enum RepositorySubjectSource
{
    /// <summary>
    /// The state of the context itself. The Workspace reads the shared link; a Feature reads its own state row, and
    /// a repository it has no state row for is on no branch and no tag (never borrowed from the Workspace).
    /// </summary>
    Context,

    /// <summary>
    /// The context's state row when one exists, in the Workspace too; without one the Workspace falls back to the
    /// shared link and a Feature has no state. Kept because the push plan has always read it this way.
    /// </summary>
    ContextStateFirst,

    /// <summary>
    /// The shared link in every context, so a Feature is judged by the Workspace checkout. Kept because the
    /// dependency update path has always read it this way; it is wrong for a Feature and is replaced on purpose later.
    /// </summary>
    LegacySharedLink,
}

/// <summary>
/// Loads the policy subjects of a Workspace's repositories for one context and applies
/// <see cref="RepositoryOperationPolicy"/> to them.
/// </summary>
public interface IWorkspaceRepositoryOperationPolicy
{
    /// <summary>The subject of every repository of the Workspace, by repository id.</summary>
    Task<IReadOnlyDictionary<int, RepositoryPolicySubject>> GetSubjectsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        RepositorySubjectSource source = RepositorySubjectSource.Context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Splits the candidates of a bulk operation. <paramref name="repositoryIds"/> null means every repository of
    /// the Workspace. Ids that are not in the Workspace are dropped.
    /// </summary>
    Task<RepositoryEligibility> PartitionAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        RepositoryOperation operation,
        IReadOnlyCollection<int>? repositoryIds = null,
        RepositorySubjectSource source = RepositorySubjectSource.Context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides one operation the user asked for on one repository. A repository that is not in the Workspace has
    /// no state to object with and is allowed; the caller reports the missing repository itself.
    /// </summary>
    Task<RepositoryOperationDecision> EvaluateAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        RepositoryOperation operation,
        int repositoryId,
        RepositorySubjectSource source = RepositorySubjectSource.Context,
        CancellationToken cancellationToken = default);
}
