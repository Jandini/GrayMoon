namespace GrayMoon.Agent.Services.GitChanges;

/// <summary>
/// One working-tree filesystem observation recorded at watcher receipt time. Invalidation metadata only -
/// never used to reconstruct Git state (authoritative status always comes from a fresh scan).
/// </summary>
public sealed record GitRepositoryObservedChange
{
    public required DateTimeOffset ObservedAt { get; init; }
    public required string Path { get; init; }
    public required GitRepositoryObservedChangeKind Kind { get; init; }
    public string? OldPath { get; init; }
}
