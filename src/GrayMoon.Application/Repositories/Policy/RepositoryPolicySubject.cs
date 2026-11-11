namespace GrayMoon.Application.Repositories.Policy;

/// <summary>
/// The state of one repository in one context that decides what may be done to it. Built by the caller from the
/// row it already trusts for that context (the shared link, the context state, a list item), never looked up here.
/// </summary>
/// <param name="IsWorkspaceRepository">True for the Workspace repository, false for a Source.</param>
/// <param name="IsReference">Reference (read-only) mode. Always false until reference mode exists.</param>
/// <param name="BranchName">The checked-out branch, or null.</param>
/// <param name="CheckedOutTag">The checked-out tag, or null. Blank counts as no tag.</param>
/// <param name="IsFeatureContext">True when the state is that of a Feature, false for the Workspace itself.</param>
/// <param name="IsReferenceSnapshot">A reference repository held detached by a Feature. Always false until reference mode exists.</param>
public sealed record RepositoryPolicySubject(
    bool IsWorkspaceRepository,
    bool IsReference,
    string? BranchName,
    string? CheckedOutTag,
    bool IsFeatureContext,
    bool IsReferenceSnapshot)
{
    /// <summary>The one tag test: null, empty and whitespace-only all mean "not on a tag".</summary>
    public bool IsOnTag => !string.IsNullOrWhiteSpace(CheckedOutTag);

    /// <summary>A Source in the given checkout state, with reference mode off.</summary>
    public static RepositoryPolicySubject ForCheckout(
        string? branchName,
        string? checkedOutTag,
        bool isWorkspaceRepository = false,
        bool isFeatureContext = false) =>
        new(isWorkspaceRepository, IsReference: false, branchName, checkedOutTag, isFeatureContext, IsReferenceSnapshot: false);
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
