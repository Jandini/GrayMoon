using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using GrayMoon.Application.Repositories.Policy;

namespace GrayMoon.App.Services.Application;

public sealed class WorkspacePullRequestOperations(
    IPullRequestService pullRequestService,
    WorkspacePullRequestService workspacePullRequestService,
    IWorkspaceRepositoryOperationPolicy operationPolicy,
    IWorkspaceFeatureContextResolver contextResolver) : IWorkspacePullRequestOperations
{
    public async Task<IReadOnlyList<CreatePullRequestResult>> CreateAsync(
        int workspaceId,
        IReadOnlyList<CreatePullRequestRequest> requests,
        IProgress<CreatePullRequestProgress>? progress,
        CancellationToken cancellationToken)
    {
        var refusals = await GetReferenceRefusalsAsync(
            workspaceId, RepositoryOperation.CreatePullRequest, requests.Select(request => request.RepositoryId), cancellationToken);
        if (refusals.Count == 0)
            return await pullRequestService.CreatePullRequestsAsync(requests, progress, cancellationToken);

        var allowed = requests.Where(request => !refusals.ContainsKey(request.RepositoryId)).ToList();
        var created = allowed.Count == 0
            ? []
            : await pullRequestService.CreatePullRequestsAsync(allowed, progress, cancellationToken);

        var refused = requests
            .Where(request => refusals.ContainsKey(request.RepositoryId))
            .Select(request => new CreatePullRequestResult
            {
                RepositoryId = request.RepositoryId,
                RepositoryName = request.RepositoryName,
                Success = false,
                ErrorMessage = refusals[request.RepositoryId],
            });
        return [.. created, .. refused];
    }

    public async Task<MergeResult> MergeAsync(
        int workspaceId,
        int repositoryId,
        int prNumber,
        MergeMethod method,
        string? expectedHeadSha,
        CancellationToken cancellationToken)
    {
        var refusals = await GetReferenceRefusalsAsync(
            workspaceId, RepositoryOperation.MergePullRequest, [repositoryId], cancellationToken);
        if (refusals.TryGetValue(repositoryId, out var refusal))
            return new MergeResult(false, refusal);

        return await workspacePullRequestService.MergePullRequestAsync(
            workspaceId,
            repositoryId,
            prNumber,
            method,
            expectedHeadSha,
            cancellationToken);
    }

    public async Task<IReadOnlyList<MergePullRequestResult>> MergeManyAsync(
        int workspaceId,
        IReadOnlyList<MergePullRequestRequest> requests,
        IProgress<MergePullRequestProgress>? progress,
        CancellationToken cancellationToken)
    {
        var refusals = await GetReferenceRefusalsAsync(
            workspaceId, RepositoryOperation.MergePullRequest, requests.Select(request => request.RepositoryId), cancellationToken);
        if (refusals.Count == 0)
            return await workspacePullRequestService.MergePullRequestsAsync(workspaceId, requests, progress, cancellationToken);

        var allowed = requests.Where(request => !refusals.ContainsKey(request.RepositoryId)).ToList();
        var merged = allowed.Count == 0
            ? []
            : await workspacePullRequestService.MergePullRequestsAsync(workspaceId, allowed, progress, cancellationToken);

        var refused = requests
            .Where(request => refusals.ContainsKey(request.RepositoryId))
            .Select(request => new MergePullRequestResult
            {
                RepositoryId = request.RepositoryId,
                PrNumber = request.PrNumber,
                Success = false,
                ErrorMessage = refusals[request.RepositoryId],
            });
        return [.. merged, .. refused];
    }

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

    /// <summary>
    /// The repositories that take no part in <paramref name="operation"/> because they are read-only, with the message
    /// to report. A pull request call names no context, and whether a repository is read-only is the same in every
    /// context, so only that is judged here; what a context has checked out stays the caller's check, as before.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, string>> GetReferenceRefusalsAsync(
        int workspaceId,
        RepositoryOperation operation,
        IEnumerable<int> repositoryIds,
        CancellationToken cancellationToken)
    {
        var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var subjects = await operationPolicy.GetSubjectsAsync(workspaceId, contextId, cancellationToken: cancellationToken);

        var refusals = new Dictionary<int, string>();
        foreach (var repositoryId in repositoryIds.Distinct())
        {
            if (!subjects.TryGetValue(repositoryId, out var subject))
                continue;

            var decision = RepositoryOperationPolicy.Evaluate(
                operation,
                RepositoryOperationScope.Individual,
                subject with { BranchName = null, CheckedOutTag = null });
            if (!decision.IsAllowed)
                refusals[repositoryId] = decision.Message ?? "The operation is not possible for this repository.";
        }

        return refusals;
    }
}
