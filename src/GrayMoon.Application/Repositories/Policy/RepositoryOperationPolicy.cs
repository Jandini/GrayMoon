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

    /// <summary>One row per operation. <see cref="AllowedOnTag"/> is what GrayMoon does today for a tag checkout.</summary>
    public sealed record Rule(bool AllowedOnTag, string OnTagMessage = RepositoryOperationPolicy.OnTagMessage);

    /// <summary>
    /// The rule table. It is data so that a test can enumerate it; adding a <see cref="RepositoryOperation"/>
    /// without a row here fails that test.
    /// </summary>
    public static FrozenDictionary<RepositoryOperation, Rule> Rules { get; } = new Dictionary<RepositoryOperation, Rule>
    {
        [RepositoryOperation.Sync] = new(AllowedOnTag: true),
        [RepositoryOperation.Fetch] = new(AllowedOnTag: true),
        [RepositoryOperation.RefreshProjects] = new(AllowedOnTag: false),
        [RepositoryOperation.RestorePackages] = new(AllowedOnTag: false),

        [RepositoryOperation.Pull] = new(AllowedOnTag: false),

        // Moving off a tag is how a repository leaves it, so the checkouts and branch creation stay possible.
        // "Skip repos on tags" in the Workspace-wide dialogs is a user option, not a rule: see IsOnTag on the subject.
        [RepositoryOperation.ManualCheckout] = new(AllowedOnTag: true),
        [RepositoryOperation.WorkspaceCheckout] = new(AllowedOnTag: true),
        [RepositoryOperation.CreateBranch] = new(AllowedOnTag: true),
        [RepositoryOperation.ReturnToDefault] = new(AllowedOnTag: false),
        [RepositoryOperation.ReturnToDefaultCleanup] = new(AllowedOnTag: false),

        [RepositoryOperation.DeleteLocalBranch] = new(AllowedOnTag: true),
        [RepositoryOperation.DeleteBranchRemoteOrForced] = new(AllowedOnTag: true),
        [RepositoryOperation.UpdateBranchFromDefault] = new(AllowedOnTag: false),
        [RepositoryOperation.UpdateDependencies] = new(AllowedOnTag: false),
        [RepositoryOperation.UpdateVersionFiles] = new(AllowedOnTag: false),
        [RepositoryOperation.Commit] = new(AllowedOnTag: false),
        [RepositoryOperation.Push] = new(AllowedOnTag: false, OnTagPushMessage),
        [RepositoryOperation.SetUpstream] = new(AllowedOnTag: true),
        [RepositoryOperation.UndoPush] = new(AllowedOnTag: false),
        [RepositoryOperation.CreatePullRequest] = new(AllowedOnTag: false),
        [RepositoryOperation.MergePullRequest] = new(AllowedOnTag: false),
        [RepositoryOperation.RunWorkflow] = new(AllowedOnTag: true),

        [RepositoryOperation.Stage] = new(AllowedOnTag: true),
        [RepositoryOperation.Unstage] = new(AllowedOnTag: true),
        [RepositoryOperation.Discard] = new(AllowedOnTag: true),

        // A repository on a tag joins a Feature detached, without a Feature branch.
        [RepositoryOperation.CreateFeatureBranch] = new(AllowedOnTag: false),
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

        return RepositoryOperationDecision.Allowed;
    }

    /// <summary>True when the operation may run. The scope changes how a "no" is reported, never the answer.</summary>
    public static bool IsAllowed(RepositoryOperation operation, RepositoryPolicySubject subject) =>
        Evaluate(operation, RepositoryOperationScope.Bulk, subject).IsAllowed;

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
