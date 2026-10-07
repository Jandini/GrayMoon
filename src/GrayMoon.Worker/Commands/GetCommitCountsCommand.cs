using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>Returns only outgoing and incoming commit counts for the current branch. Used after push to refresh counts without running GitVersion or branch listing.</summary>
public sealed class GetCommitCountsCommand(IGitRepositoryReader reader) : ICommandHandler<GetCommitCountsRequest, GetCommitCountsResponse>
{
    public async Task<GetCommitCountsResponse> ExecuteAsync(GetCommitCountsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
            return new GetCommitCountsResponse();

        var branch = await reader.GetCurrentBranchNameAsync(repoPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(branch))
            return new GetCommitCountsResponse();

        var defaultRef = await reader.GetDefaultBranchOriginRefAsync(repoPath, cancellationToken);
        // Read-only: do not clear/persist here (Return-to-default safety checks use this command).
        var divergenceRef = OriginDefaultRef.ToOriginBranchRef(request.DivergenceBaseBranch)
            ?? OriginDefaultRef.ToOriginBranchRef(await reader.GetDivergenceBaseBranchAsync(repoPath, cancellationToken))
            ?? defaultRef;
        var (outgoing, incoming, hasUpstream) = await reader.GetCommitCountsAsync(repoPath, branch, defaultRef, cancellationToken);
        var (defaultBehind, defaultAhead, _) = await reader.GetCommitCountsVsDefaultAsync(repoPath, divergenceRef, cancellationToken);
        return new GetCommitCountsResponse
        {
            OutgoingCommits = outgoing,
            IncomingCommits = incoming,
            HasUpstream = hasUpstream,
            DefaultBranchBehind = defaultBehind,
            DefaultBranchAhead = defaultAhead
        };
    }
}
