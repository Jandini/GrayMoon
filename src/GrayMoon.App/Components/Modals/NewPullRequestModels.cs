namespace GrayMoon.App.Components.Modals;

/// <summary>Per-repository input for the New Pull Request modal.</summary>
public sealed record NewPrTargetRepo(
    int RepositoryId,
    string Owner,
    string RepositoryName,
    string HeadBranch,
    string BaseBranch,
    string? CloneUrl);

/// <summary>Result emitted by the New Pull Request modal when the user clicks Create.</summary>
public sealed record NewPrFormResult(
    string Title,
    string? Body,
    bool IsDraft,
    IReadOnlyList<string> Reviewers,
    IReadOnlyList<string> TeamReviewers);
