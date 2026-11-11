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
            isFeatureContext: isFeatureContext);

    /// <summary>The subject of a list row, which already carries the selected context's branch and tag.</summary>
    public static RepositoryPolicySubject ToPolicySubject(this WorkspaceRepositoryLinkListItemDto row, bool isFeatureContext) =>
        RepositoryPolicySubject.ForCheckout(
            row.BranchName,
            row.CheckedOutTag,
            isWorkspaceRepository: row.Role == WorkspaceRepositoryRole.Workspace,
            isFeatureContext: isFeatureContext);

    /// <summary>True when <paramref name="operation"/> may run on the repository this link describes.</summary>
    public static bool Allows(this WorkspaceRepositoryLink link, RepositoryOperation operation, bool isFeatureContext) =>
        RepositoryOperationPolicy.IsAllowed(operation, link.ToPolicySubject(isFeatureContext));

    /// <summary>True when <paramref name="operation"/> may run on the repository this list row describes.</summary>
    public static bool Allows(this WorkspaceRepositoryLinkListItemDto row, RepositoryOperation operation, bool isFeatureContext) =>
        RepositoryOperationPolicy.IsAllowed(operation, row.ToPolicySubject(isFeatureContext));

    /// <summary>
    /// For a caller that was handed rows without the kind of context they came from. No rule reads the context kind
    /// for the operations these callers ask about; a rule that starts to must be given the kind by its caller.
    /// </summary>
    public static bool Allows(this WorkspaceRepositoryLink link, RepositoryOperation operation) =>
        link.Allows(operation, isFeatureContext: false);

    /// <inheritdoc cref="Allows(WorkspaceRepositoryLink, RepositoryOperation)"/>
    public static bool Allows(this WorkspaceRepositoryLinkListItemDto row, RepositoryOperation operation) =>
        row.Allows(operation, isFeatureContext: false);
}
