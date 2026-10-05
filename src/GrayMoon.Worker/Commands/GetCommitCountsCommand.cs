using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

/// <summary>Returns only outgoing and incoming commit counts for the current branch. Used after push to refresh counts without running GitVersion or branch listing.</summary>
public sealed class GetCommitCountsCommand(IGitService git) : ICommandHandler<GetCommitCountsRequest, GetCommitCountsResponse>
{
    public async Task<GetCommitCountsResponse> ExecuteAsync(GetCommitCountsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
            return new GetCommitCountsResponse();

        var branch = await git.GetCurrentBranchNameAsync(repoPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(branch))
            return new GetCommitCountsResponse();

        var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, cancellationToken);
        // Read-only: do not clear/persist here (Return-to-default safety checks use this command).
        var divergenceRef = git.ToOriginBranchRef(request.DivergenceBaseBranch)
            ?? git.ToOriginBranchRef(await git.GetDivergenceBaseBranchAsync(repoPath, cancellationToken))
            ?? defaultRef;
        var (outgoing, incoming, hasUpstream) = await git.GetCommitCountsAsync(repoPath, branch, defaultRef, cancellationToken);
        var (defaultBehind, defaultAhead, _) = await git.GetCommitCountsVsDefaultAsync(repoPath, divergenceRef, cancellationToken);
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
