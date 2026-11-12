using System.Collections.Frozen;

namespace GrayMoon.Application.Repositories.Policy;

/// <summary>
/// The one place that says whether an operation may run on a repository. Pure: no database, no services, so pages,
/// services and tests all ask the same question and get the same answer.
/// </summary>
public static class RepositoryOperationPolicy
{
    /// <summary>The message for an operation that is not possible on a tag.</summary>
    public const string OnTagMessage = "Repository is on a tag; checkout a branch first.";

    /// <summary>The message for a push of a repository that is on a tag.</summary>
    public const string OnTagPushMessage = "Repository is pinned to a tag. Checkout a branch before pushing.";

    /// <summary>The message for an operation that a reference (read-only) repository does not take part in.</summary>
    public const string ReferenceMessage = "Repository is read-only.";

    /// <summary>The message for an operation on a repository a Feature holds as a detached read-only snapshot.</summary>
    public const string ReferenceSnapshotMessage = "Repository is a read-only snapshot in this Feature.";

    /// <summary>One row per operation.</summary>
    /// <param name="AllowedOnTag">What GrayMoon does for a tag checkout.</param>
    /// <param name="Reference">What a reference repository on a branch allows.</param>
    /// <param name="ReferenceSnapshot">What a reference repository held detached by a Feature allows.</param>
    public sealed record Rule(
        bool AllowedOnTag,
        ReferenceRule Reference,
        ReferenceRule ReferenceSnapshot,
        string OnTagMessage = RepositoryOperationPolicy.OnTagMessage);

    /// <summary>
    /// The rule table. It is data so that a test can enumerate it; adding a <see cref="RepositoryOperation"/>
    /// without a row here fails that test.
    /// </summary>
    public static FrozenDictionary<RepositoryOperation, Rule> Rules { get; } = new Dictionary<RepositoryOperation, Rule>
    {
        [RepositoryOperation.Sync] = new(AllowedOnTag: true, ReferenceRule.Allowed, ReferenceRule.Allowed),
        [RepositoryOperation.Fetch] = new(AllowedOnTag: true, ReferenceRule.Allowed, ReferenceRule.Allowed),
        [RepositoryOperation.RefreshProjects] = new(AllowedOnTag: false, ReferenceRule.Allowed, ReferenceRule.Allowed),
        [RepositoryOperation.RestorePackages] = new(AllowedOnTag: false, ReferenceRule.Allowed, ReferenceRule.Allowed),

        [RepositoryOperation.Pull] = new(AllowedOnTag: false, ReferenceRule.Allowed, ReferenceRule.Forbidden),

        // Moving off a tag is how a repository leaves it, so the checkouts and branch creation stay possible.
        // "Skip repos on tags" in the Workspace-wide dialogs is a user option, not a rule: see IsOnTag on the subject.
        [RepositoryOperation.ManualCheckout] = new(AllowedOnTag: true, ReferenceRule.Allowed, ReferenceRule.Forbidden),
        [RepositoryOperation.WorkspaceCheckout] = new(AllowedOnTag: true, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
        [RepositoryOperation.CreateBranch] = new(AllowedOnTag: true, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
        [RepositoryOperation.ReturnToDefault] = new(AllowedOnTag: false, ReferenceRule.IndividualOnly, ReferenceRule.Forbidden),
        [RepositoryOperation.ReturnToDefaultCleanup] = new(AllowedOnTag: false, ReferenceRule.Forbidden, ReferenceRule.Forbidden),

        [RepositoryOperation.DeleteLocalBranch] = new(AllowedOnTag: true, ReferenceRule.Allowed, ReferenceRule.Forbidden),
        [RepositoryOperation.DeleteBranchRemoteOrForced] = new(AllowedOnTag: true, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
        [RepositoryOperation.UpdateBranchFromDefault] = new(AllowedOnTag: false, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
        [RepositoryOperation.UpdateDependencies] = new(AllowedOnTag: false, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
        [RepositoryOperation.UpdateVersionFiles] = new(AllowedOnTag: false, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
        [RepositoryOperation.Commit] = new(AllowedOnTag: false, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
        // A commit the user makes in Git Changes. A tag checkout has always allowed it; the commit GrayMoon makes after
        // a dependency or version file update is the row above.
        [RepositoryOperation.CommitGitChanges] = new(AllowedOnTag: true, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
        [RepositoryOperation.Push] = new(AllowedOnTag: false, ReferenceRule.Forbidden, ReferenceRule.Forbidden, OnTagPushMessage),
        [RepositoryOperation.SetUpstream] = new(AllowedOnTag: true, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
        [RepositoryOperation.UndoPush] = new(AllowedOnTag: false, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
        [RepositoryOperation.CreatePullRequest] = new(AllowedOnTag: false, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
        [RepositoryOperation.MergePullRequest] = new(AllowedOnTag: false, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
        [RepositoryOperation.RunWorkflow] = new(AllowedOnTag: true, ReferenceRule.Forbidden, ReferenceRule.Forbidden),

        [RepositoryOperation.Stage] = new(AllowedOnTag: true, ReferenceRule.IndividualOnly, ReferenceRule.IndividualOnly),
        [RepositoryOperation.Unstage] = new(AllowedOnTag: true, ReferenceRule.IndividualOnly, ReferenceRule.IndividualOnly),
        [RepositoryOperation.Discard] = new(AllowedOnTag: true, ReferenceRule.IndividualOnly, ReferenceRule.IndividualOnly),

        // A repository on a tag joins a Feature detached, without a Feature branch.
        [RepositoryOperation.CreateFeatureBranch] = new(AllowedOnTag: false, ReferenceRule.Forbidden, ReferenceRule.Forbidden),
    }.ToFrozenDictionary();

    /// <summary>
    /// Decides one operation for one repository. <see cref="RepositoryOperationScope.Individual"/> never answers
    /// <see cref="RepositoryOperationOutcome.Skipped"/> and <see cref="RepositoryOperationScope.Bulk"/> never answers
    /// <see cref="RepositoryOperationOutcome.Refused"/>.
    /// </summary>
    public static RepositoryOperationDecision Evaluate(
        RepositoryOperation operation,
        RepositoryOperationScope scope,
        RepositoryPolicySubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (!Rules.TryGetValue(operation, out var rule))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "The operation has no policy rule.");

        if (subject.IsOnTag && !rule.AllowedOnTag)
            return Forbidden(scope, RepositoryOperationReason.OnTag, rule.OnTagMessage);

        // The Workspace repository is where GrayMoon writes the Workspace definition: it is never a reference repository.
        if (subject.IsWorkspaceRepository)
            return RepositoryOperationDecision.Allowed;

        if (subject.IsReferenceSnapshot && !Permits(rule.ReferenceSnapshot, scope))
            return Forbidden(scope, RepositoryOperationReason.ReferenceSnapshot, ReferenceSnapshotMessage);

        if (subject.IsReference && !Permits(rule.Reference, scope))
            return Forbidden(scope, RepositoryOperationReason.Reference, ReferenceMessage);

        return RepositoryOperationDecision.Allowed;
    }

    /// <summary>
    /// True when the operation may run. For a tag the scope changes only how a "no" is reported. A reference
    /// repository allows a few operations (Return to Default as a plain switch, stage, unstage, discard) when the
    /// user names the repository and leaves them out of a bulk action, so a caller acting on one named repository
    /// passes <see cref="RepositoryOperationScope.Individual"/>.
    /// </summary>
    public static bool IsAllowed(
        RepositoryOperation operation,
        RepositoryPolicySubject subject,
        RepositoryOperationScope scope = RepositoryOperationScope.Bulk) =>
        Evaluate(operation, scope, subject).IsAllowed;

    /// <summary>True when a rule cell allows the operation in the given scope.</summary>
    public static bool Permits(ReferenceRule rule, RepositoryOperationScope scope) =>
        rule == ReferenceRule.Allowed
        || (rule == ReferenceRule.IndividualOnly && scope == RepositoryOperationScope.Individual);

    private static RepositoryOperationDecision Forbidden(
        RepositoryOperationScope scope,
        RepositoryOperationReason reason,
        string message) =>
        new(
            scope == RepositoryOperationScope.Individual
                ? RepositoryOperationOutcome.Refused
                : RepositoryOperationOutcome.Skipped,
            reason,
            message);
}
