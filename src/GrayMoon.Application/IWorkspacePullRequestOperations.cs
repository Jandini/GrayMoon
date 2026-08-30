using GrayMoon.App.Models;
using GrayMoon.App.Services.GitHub;

namespace GrayMoon.Application;

public interface IWorkspacePullRequestOperations
{
    Task<IReadOnlyList<CreatePullRequestResult>> CreateAsync(
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
