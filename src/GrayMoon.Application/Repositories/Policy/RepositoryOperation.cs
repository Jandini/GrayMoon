namespace GrayMoon.Application.Repositories.Policy;

/// <summary>
/// Everything GrayMoon can do to one repository that a repository's state can forbid. One value per rule row of
/// <see cref="RepositoryOperationPolicy"/>; a value without a row fails the policy tests.
/// </summary>
public enum RepositoryOperation
{
    // Read and refresh.
    Sync,
    Fetch,
    RefreshProjects,
    RestorePackages,

    // Incoming changes.
    Pull,

    // Checkout.
    ManualCheckout,
    WorkspaceCheckout,
    ReturnToDefault,
    ReturnToDefaultCleanup,

    // Development writes.
    CreateBranch,
    DeleteLocalBranch,
    DeleteBranchRemoteOrForced,
    UpdateBranchFromDefault,
    UpdateDependencies,
    UpdateVersionFiles,
    Commit,
    Push,
    SetUpstream,
    UndoPush,
    CreatePullRequest,
    MergePullRequest,
    RunWorkflow,

    // Local change management.
    Stage,
    Unstage,
    Discard,

    // Features.
    CreateFeatureBranch,
}

/// <summary>Whether the user asked for this one repository, or it is one of many in a Workspace-wide action.</summary>
public enum RepositoryOperationScope
{
    /// <summary>The user named this repository. A forbidden operation is refused with a message.</summary>
    Individual,

    /// <summary>The repository is one candidate of a bulk action. A forbidden operation is skipped and the rest run.</summary>
    Bulk,
}

public enum RepositoryOperationOutcome
{
    Allowed,

    /// <summary>Only for <see cref="RepositoryOperationScope.Individual"/>.</summary>
    Refused,

    /// <summary>Only for <see cref="RepositoryOperationScope.Bulk"/>. The caller reports it and does not count it as done.</summary>
    Skipped,
}

public enum RepositoryOperationReason
{
    None,
    OnTag,
    Reference,
    ReferenceSnapshot,
    WorkspaceRepository,
    NotOnBranch,
}
