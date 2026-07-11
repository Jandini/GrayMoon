using GrayMoon.Common.Git;

namespace GrayMoon.Agent.Abstractions;

/// <summary>
/// Git-status/diff/mutation operations for the Git Changes feature, implemented against the native
/// git CLI. All paths passed in requests are repository-relative and are validated with
/// <see cref="GitRepositoryPathValidator"/> before touching git or the filesystem.
/// </summary>
public interface IRepositoryGitChangesService
{
    Task<GitChangeStatusResult> GetStatusAsync(string repoPath, long snapshotVersion, CancellationToken cancellationToken);

    Task<GitDiffDocument> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken cancellationToken);

    Task<GitMutationResult> StageAsync(string repoPath, GitStageOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken);

    Task<GitMutationResult> UnstageAsync(string repoPath, GitStageOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken);

    Task<GitCommitResult> CommitAsync(string repoPath, GitCommitOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken);
}

public sealed record GitChangeStatusResult
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public GitChangeSnapshot? Snapshot { get; init; }
}

public sealed record GitDiffRequest(string? Path, GitDiffComparison Comparison);

public sealed record GitStageOperationRequest(GitChangeOperationScope Scope, IReadOnlyList<string> Paths);

public sealed record GitCommitOperationRequest(string CommitMessage, bool StageAllFirst);
