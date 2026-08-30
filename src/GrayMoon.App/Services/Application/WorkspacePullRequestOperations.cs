using GrayMoon.App.Models;

namespace GrayMoon.App.Services.Application;

public sealed class WorkspacePullRequestOperations(
    IPullRequestService pullRequestService,
    WorkspacePullRequestService workspacePullRequestService) : IWorkspacePullRequestOperations
{
    public Task<IReadOnlyList<CreatePullRequestResult>> CreateAsync(
        IReadOnlyList<CreatePullRequestRequest> requests,
        IProgress<CreatePullRequestProgress>? progress,
        CancellationToken cancellationToken)
        => pullRequestService.CreatePullRequestsAsync(requests, progress, cancellationToken);

    public Task<MergeResult> MergeAsync(
        int workspaceId,
        int repositoryId,
        int prNumber,
        MergeMethod method,
        string? expectedHeadSha,
        CancellationToken cancellationToken)
        => workspacePullRequestService.MergePullRequestAsync(
            workspaceId,
            repositoryId,
            prNumber,
            method,
            expectedHeadSha,
            cancellationToken);

    public Task<MergeResult> UpdateTitleAsync(
        int workspaceId,
        int repositoryId,
        int prNumber,
        string title,
        CancellationToken cancellationToken)
        => workspacePullRequestService.UpdatePullRequestTitleAsync(
            workspaceId,
            repositoryId,
            prNumber,
            title,
            cancellationToken);

    public Task<MergeResult> CloseAsync(
        int workspaceId,
        int repositoryId,
        int prNumber,
        CancellationToken cancellationToken)
        => workspacePullRequestService.ClosePullRequestAsync(
            workspaceId,
            repositoryId,
            prNumber,
            cancellationToken);
}
