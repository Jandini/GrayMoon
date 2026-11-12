using GrayMoon.App.Models;
using GrayMoon.App.Services.GitHub;

namespace GrayMoon.Application;

public interface IWorkspacePullRequestOperations
{
    /// <summary>
    /// Creates the pull requests. A request for a read-only repository of the Workspace is not sent; it comes back
    /// as a failed result with the reason.
    /// </summary>
    Task<IReadOnlyList<CreatePullRequestResult>> CreateAsync(
        int workspaceId,
        IReadOnlyList<CreatePullRequestRequest> requests,
        IProgress<CreatePullRequestProgress>? progress,
        CancellationToken cancellationToken);

    Task<MergeResult> MergeAsync(
        int workspaceId,
        int repositoryId,
        int prNumber,
        MergeMethod method,
        string? expectedHeadSha,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MergePullRequestResult>> MergeManyAsync(
        int workspaceId,
        IReadOnlyList<MergePullRequestRequest> requests,
        IProgress<MergePullRequestProgress>? progress,
        CancellationToken cancellationToken);

    Task<MergeResult> UpdateTitleAsync(
        int workspaceId,
        int repositoryId,
        int prNumber,
        string title,
        CancellationToken cancellationToken);

    Task<MergeResult> CloseAsync(
        int workspaceId,
        int repositoryId,
        int prNumber,
        CancellationToken cancellationToken);
}
