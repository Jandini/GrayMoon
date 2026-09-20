namespace GrayMoon.Application.Features;

public interface IWorkspaceExternalWorktreeOperations
{
    Task<ExternalWorktreeCleanupPlan> AnalyzeExternalWorktreeCleanupAsync(
        int workspaceId,
        int workspaceRepositoryId,
        string worktreePath,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RemoveExternalWorktreeAsync(
        int workspaceId,
        int workspaceRepositoryId,
        string worktreePath,
        ExternalWorktreeCleanupOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ExternalWorktreeCleanupOptions
{
    public bool AllowForceRemoveDirty { get; init; }
    public bool DeleteLocalBranch { get; init; }
    public bool AllowForceDeleteLocalBranch { get; init; }
}

public sealed class ExternalWorktreeCleanupPlan
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string RepositoryName { get; init; } = "";
    public string? BranchName { get; init; }
    public string WorktreePath { get; init; } = "";
    public string? HeadCommit { get; init; }
    public bool WorktreeExists { get; init; }
    public bool IsDirty { get; init; }
    public bool CanRemoveNormally { get; init; }
    public bool RequiresForce { get; init; }
    public string Summary { get; init; } = "";
}

public interface IWorkspaceNativeLaunchService
{
    Task<NativeLaunchPaths?> ResolveContextLaunchPathsAsync(
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default);
}

public sealed class NativeLaunchPaths
{
    public required string ContextRoot { get; init; }
    public IReadOnlyList<NativeLaunchRepositoryPath> Repositories { get; init; } = [];
}

public sealed class NativeLaunchRepositoryPath
{
    public required int WorkspaceRepositoryId { get; init; }
    public required string RepositoryName { get; init; }
    public required string Path { get; init; }
}
