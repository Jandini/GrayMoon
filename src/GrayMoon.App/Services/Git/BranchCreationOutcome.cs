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
    public static BranchCreationOutcome Empty { get; } =
        new(new HashSet<int>(), new Dictionary<int, string?>(), new Dictionary<int, string>());
}
