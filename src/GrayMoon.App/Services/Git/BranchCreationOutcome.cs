namespace GrayMoon.App.Services.Git;

/// <summary>
/// What a bulk branch creation did. <see cref="TargetedRepositoryIds"/> are the repositories it attempted. A successful
/// repository is in <see cref="CheckedOutBranchByRepositoryId"/> with the branch the Worker reported checked out
/// afterwards (null when it did not say); a failed one is in <see cref="ErrorsByRepositoryId"/>. A targeted repository
/// in neither has no known outcome.
/// </summary>
public sealed record BranchCreationOutcome(
    IReadOnlySet<int> TargetedRepositoryIds,
    IReadOnlyDictionary<int, string?> CheckedOutBranchByRepositoryId,
    IReadOnlyDictionary<int, string> ErrorsByRepositoryId)
{
    /// <summary>Repositories the policy left out (reference repositories). They were not attempted and are not targeted.</summary>
    public IReadOnlyList<GrayMoon.Application.Repositories.Policy.RepositorySkip> Skipped { get; init; } = [];

    /// <summary>
    /// Successful repositories (id to name) where a local branch of that name was already there. It was checked out as
    /// it is and the requested base was not used, so the caller must report it (<see cref="GrayMoon.Application.BranchAlreadyExistedWarning"/>).
    /// </summary>
    public IReadOnlyDictionary<int, string> AlreadyExistedByRepositoryId { get; init; } = new Dictionary<int, string>();

    public static BranchCreationOutcome Empty { get; } =
        new(new HashSet<int>(), new Dictionary<int, string?>(), new Dictionary<int, string>());
}
