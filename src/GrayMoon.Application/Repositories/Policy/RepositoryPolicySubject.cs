namespace GrayMoon.Application.Repositories.Policy;

/// <summary>
/// The state of one repository in one context that decides what may be done to it. Built by the caller from the
/// row it already trusts for that context (the shared link, the context state, a list item), never looked up here.
/// </summary>
/// <param name="IsWorkspaceRepository">True for the Workspace repository, false for a Source.</param>
/// <param name="IsReference">True when the Source is a reference (read-only) repository. Shared by every context.</param>
/// <param name="BranchName">The checked-out branch, or null.</param>
/// <param name="CheckedOutTag">The checked-out tag, or null. Blank counts as no tag.</param>
/// <param name="IsFeatureContext">True when the state is that of a Feature, false for the Workspace itself.</param>
/// <param name="IsReferenceSnapshot">True when a Feature holds the repository as a detached reference snapshot.</param>
public sealed record RepositoryPolicySubject(
    bool IsWorkspaceRepository,
    bool IsReference,
    string? BranchName,
    string? CheckedOutTag,
    bool IsFeatureContext,
    bool IsReferenceSnapshot)
{
    /// <summary>
    /// True when the provider reported the repository as archived. A fact about the repository, the same in every
    /// Workspace and context. It is the value of the last fetch or check, so it can be out of date.
    /// </summary>
    public bool IsArchived { get; init; }

    /// <summary>The one tag test: null, empty and whitespace-only all mean "not on a tag".</summary>
    public bool IsOnTag => !string.IsNullOrWhiteSpace(CheckedOutTag);

    /// <summary>A repository in the given checkout state.</summary>
    public static RepositoryPolicySubject ForCheckout(
        string? branchName,
        string? checkedOutTag,
        bool isWorkspaceRepository = false,
        bool isFeatureContext = false,
        bool isReference = false,
        bool isReferenceSnapshot = false,
        bool isArchived = false) =>
        new(isWorkspaceRepository, isReference, branchName, checkedOutTag, isFeatureContext, isReferenceSnapshot) { IsArchived = isArchived };
}

/// <summary>The answer for one operation on one repository.</summary>
/// <param name="Message">What to tell the user; null when the operation is allowed.</param>
public sealed record RepositoryOperationDecision(
    RepositoryOperationOutcome Outcome,
    RepositoryOperationReason Reason,
    string? Message)
{
    public static RepositoryOperationDecision Allowed { get; } =
        new(RepositoryOperationOutcome.Allowed, RepositoryOperationReason.None, null);

    public bool IsAllowed => Outcome == RepositoryOperationOutcome.Allowed;
}

/// <summary>A repository a bulk action left out, and why.</summary>
public sealed record RepositorySkip(
    int RepositoryId,
    string RepositoryName,
    RepositoryOperationReason Reason,
    string Message);

/// <summary>The candidates of a bulk action, split by the policy. Order follows the input.</summary>
public sealed record RepositoryEligibility(
    IReadOnlyList<int> Eligible,
    IReadOnlyList<RepositorySkip> Skipped)
{
    public static RepositoryEligibility Empty { get; } = new([], []);
}
