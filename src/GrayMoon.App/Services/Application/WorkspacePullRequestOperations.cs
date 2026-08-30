using GrayMoon.App.Models;

namespace GrayMoon.App.Services.Application;

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
}

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
}
