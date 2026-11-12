namespace GrayMoon.Application.Repositories.Policy;

/// <summary>
/// Everything GrayMoon can do to one repository that a repository's state can forbid. One value per rule row of
/// <see cref="RepositoryOperationPolicy"/>; a value without a row fails the policy tests.
/// </summary>
public enum RepositoryOperation
{
    // Read and refresh.
    Sync,
    /// <summary>Sync started from one row. Unlike <see cref="Sync"/> for the whole Workspace it leaves a tag checkout alone.</summary>
    SyncFromRow,
    /// <summary>Sync started for one dependency level. It leaves a tag checkout alone, like <see cref="SyncFromRow"/>.</summary>
    SyncLevel,
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
    CommitGitChanges,
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

/// <summary>What a reference repository allows for one operation.</summary>
public enum ReferenceRule
{
    Allowed,

    /// <summary>Allowed when the user names the repository; left out of a bulk action.</summary>
    IndividualOnly,

    Forbidden,
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

/// <summary>Why a repository was refused or skipped. Written by name in REST responses.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<RepositoryOperationReason>))]
public enum RepositoryOperationReason
{
    None,
    OnTag,
    Reference,
    ReferenceSnapshot,
    WorkspaceRepository,
    NotOnBranch,

    /// <summary>The repository is archived on the provider, which rejects every push.</summary>
    Archived,
}
