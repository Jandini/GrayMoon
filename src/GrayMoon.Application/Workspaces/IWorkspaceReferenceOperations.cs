namespace GrayMoon.Application.Workspaces;

/// <summary>
/// The one place that changes which Source repositories of a Workspace are read-only, and the default for
/// repositories added later. Every change runs under the Workspace's structural lock, so it never overlaps a
/// running operation. The Workspace repository is never read-only and is never part of a change.
/// </summary>
public interface IWorkspaceReferenceOperations
{
    Task<WorkspaceReferenceState> GetStateAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes every Source repository read-only or writable. With <paramref name="alsoSetDefault"/> the default for
    /// repositories added later is set to the same value. Turning read-only on checks every checkout of every
    /// affected repository first; one blocked repository refuses the whole call and nothing is changed.
    /// </summary>
    Task<WorkspaceReferenceChangeResult> SetAllAsync(
        int workspaceId,
        bool reference,
        bool alsoSetDefault,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sets only the default for repositories added later. No repository changes.</summary>
    Task<WorkspaceReferenceChangeResult> SetDefaultAsync(int workspaceId, bool referenceDefault, CancellationToken cancellationToken = default);

    /// <summary>Makes one Source repository read-only or writable. The default and the other repositories do not change.</summary>
    Task<WorkspaceReferenceChangeResult> SetRepositoryAsync(
        int workspaceId,
        int repositoryId,
        bool reference,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>How many Source repositories of a Workspace are read-only.</summary>
public enum WorkspaceReferenceAggregate
{
    /// <summary>The Workspace has no Source repository.</summary>
    NoSources,
    None,
    Mixed,
    All,
}

public sealed record WorkspaceReferenceRepositoryState(int RepositoryId, string RepositoryName, bool IsReference);

/// <param name="ReferenceDefault">Whether a repository added later starts read-only.</param>
/// <param name="HasWorkspaceRepository">False when there is no Workspace definition to record the state in.</param>
public sealed record WorkspaceReferenceState(
    bool ReferenceDefault,
    WorkspaceReferenceAggregate Aggregate,
    IReadOnlyList<WorkspaceReferenceRepositoryState> Repositories,
    bool HasWorkspaceRepository);

/// <summary>
/// One checkout that stops a repository from becoming read-only. <paramref name="FeatureName"/> is null for the
/// Workspace checkout. <paramref name="Message"/> names the repository, the checkout and the reason.
/// </summary>
public sealed record WorkspaceReferenceFinding(int RepositoryId, string RepositoryName, string? FeatureName, string Message);

/// <summary>
/// Outcome of a change. Not <see cref="Success"/> means nothing was changed: <see cref="Error"/> says why, and
/// <see cref="Blocks"/> lists every checkout that stopped it. On success <see cref="Warnings"/> holds what the user
/// should know (uncommitted changes that stay, a checkout that was not checked, a file that could not be written)
/// and <see cref="Notice"/> a standing limitation.
/// </summary>
public sealed record WorkspaceReferenceChangeResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<WorkspaceReferenceFinding> Blocks { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Notice { get; init; }

    /// <summary>Repositories whose state changed.</summary>
    public int ChangedRepositoryCount { get; init; }

    public static WorkspaceReferenceChangeResult Failed(string error) => new() { Success = false, Error = error };
}

/// <summary>The none / mixed / all summary of the Source repositories of a Workspace, and its short text.</summary>
public static class WorkspaceReferenceAggregates
{
    public static WorkspaceReferenceAggregate Of(int sourceCount, int referenceCount) =>
        sourceCount <= 0 ? WorkspaceReferenceAggregate.NoSources
        : referenceCount <= 0 ? WorkspaceReferenceAggregate.None
        : referenceCount >= sourceCount ? WorkspaceReferenceAggregate.All
        : WorkspaceReferenceAggregate.Mixed;

    /// <summary>One word for a list cell. Never a count.</summary>
    public static string Label(WorkspaceReferenceAggregate aggregate) => aggregate switch
    {
        WorkspaceReferenceAggregate.All => "All",
        WorkspaceReferenceAggregate.Mixed => "Some",
        WorkspaceReferenceAggregate.None => "None",
        _ => "-",
    };
}
