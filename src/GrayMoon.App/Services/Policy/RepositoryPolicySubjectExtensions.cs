using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application.Repositories.Policy;

namespace GrayMoon.App.Services.Policy;

/// <summary>
/// Turns the rows GrayMoon already holds into policy subjects, so a caller asks
/// <see cref="RepositoryOperationPolicy"/> instead of reading the tag itself.
/// </summary>
public static class RepositoryPolicySubjectExtensions
{
    /// <summary>
    /// The subject of a link as it stands. The caller decides which context the link's branch and tag describe:
    /// a page overlays the selected context onto it, the update path reads the Workspace's.
    /// </summary>
    public static RepositoryPolicySubject ToPolicySubject(this WorkspaceRepositoryLink link, bool isFeatureContext) =>
        RepositoryPolicySubject.ForCheckout(
            link.BranchName,
            link.CheckedOutTag,
            isWorkspaceRepository: link.Role == WorkspaceRepositoryRole.Workspace,
            isFeatureContext: isFeatureContext,
            isReference: link.IsReference,
            isReferenceSnapshot: link.IsReferenceSnapshot,
            // A link loaded without its repository says nothing about it; the service decides when the operation runs.
            isArchived: link.Repository?.Archived ?? false);

    /// <summary>The subject of a list row, which already carries the selected context's branch and tag.</summary>
    public static RepositoryPolicySubject ToPolicySubject(this WorkspaceRepositoryLinkListItemDto row, bool isFeatureContext) =>
        RepositoryPolicySubject.ForCheckout(
            row.BranchName,
            row.CheckedOutTag,
            isWorkspaceRepository: row.Role == WorkspaceRepositoryRole.Workspace,
            isFeatureContext: isFeatureContext,
            isReference: row.IsReference,
            isReferenceSnapshot: row.IsReferenceSnapshot,
            isArchived: row.Archived);

    /// <summary>True when <paramref name="operation"/> may run on the repository this link describes.</summary>
    public static bool Allows(
        this WorkspaceRepositoryLink link,
        RepositoryOperation operation,
        bool isFeatureContext,
        RepositoryOperationScope scope = RepositoryOperationScope.Bulk) =>
        RepositoryOperationPolicy.IsAllowed(operation, link.ToPolicySubject(isFeatureContext), scope);

    /// <summary>True when <paramref name="operation"/> may run on the repository this list row describes.</summary>
    public static bool Allows(
        this WorkspaceRepositoryLinkListItemDto row,
        RepositoryOperation operation,
        bool isFeatureContext,
        RepositoryOperationScope scope = RepositoryOperationScope.Bulk) =>
        RepositoryOperationPolicy.IsAllowed(operation, row.ToPolicySubject(isFeatureContext), scope);

    /// <summary>
    /// For a caller that was handed rows without the kind of context they came from. No rule reads the context kind:
    /// the row itself says whether it is a reference repository or a reference snapshot.
    /// </summary>
    public static bool Allows(
        this WorkspaceRepositoryLink link,
        RepositoryOperation operation,
        RepositoryOperationScope scope = RepositoryOperationScope.Bulk) =>
        link.Allows(operation, isFeatureContext: false, scope);

    /// <inheritdoc cref="Allows(WorkspaceRepositoryLink, RepositoryOperation, RepositoryOperationScope)"/>
    public static bool Allows(
        this WorkspaceRepositoryLinkListItemDto row,
        RepositoryOperation operation,
        RepositoryOperationScope scope = RepositoryOperationScope.Bulk) =>
        row.Allows(operation, isFeatureContext: false, scope);
}
