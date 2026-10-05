namespace GrayMoon.App.Components.Modals;

/// <summary>Per-repository input for the New Pull Request modal.</summary>
public sealed record NewPrTargetRepo(
    int RepositoryId,
    string Owner,
    string RepositoryName,
    string HeadBranch,
    /// <summary>Repository default branch - used when parent provenance is missing or no longer exists remotely.</summary>
    string DefaultBranch,
    /// <summary>
    /// Feature parent/source branch captured at Feature creation, if any. Provenance suggestion only;
    /// the modal preselects it when it still exists as a remote PR base.
    /// </summary>
    string? ParentBranchName,
    string? CloneUrl);

/// <summary>Result emitted by the New Pull Request modal when the user clicks Create (or Push &amp; Create).</summary>
public sealed record NewPrFormResult(
    string Title,
    string? Body,
    bool IsDraft,
    IReadOnlyList<string> Reviewers,
    IReadOnlyList<string> TeamReviewers,
    IReadOnlyList<int> RepositoryIdsToPush,
    /// <summary>Selected PR target/base branch per repository id.</summary>
    IReadOnlyDictionary<int, string> BaseBranchByRepositoryId);
