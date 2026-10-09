namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Handed from the dependency update to the push lane each time the update has finished a dependency level.
/// The update never touches a level again once this is raised, so the push lane may read it as final.
/// </summary>
/// <param name="Level">The dependency level that was just finished.</param>
/// <param name="RepoIds">Every repository at that level (tag-pinned repositories excluded).</param>
/// <param name="CommittedRepoIds">Repositories at that level that received an update commit (version files or csproj).</param>
/// <param name="SyncedRepoIds">Repositories at that level whose csproj dependencies were rewritten.</param>
public sealed record DependencyLevelCompletion(
    int Level,
    IReadOnlySet<int> RepoIds,
    IReadOnlySet<int> CommittedRepoIds,
    IReadOnlySet<int> SyncedRepoIds);
