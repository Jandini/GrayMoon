using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class RefreshRepositoryVersionCommand(IGitService git, IAgentTokenProvider tokenProvider) : ICommandHandler<RefreshRepositoryVersionRequest, RefreshRepositoryVersionResponse>
{
    public async Task<RefreshRepositoryVersionResponse> ExecuteAsync(RefreshRepositoryVersionRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        var version = "-";
        var branch = "-";
        int? outgoingCommits = null;
        int? incomingCommits = null;
        if (git.DirectoryExists(repoPath))
        {
            var (vr, versionError) = await git.GetVersionAsync(repoPath, cancellationToken);
            if (vr != null)
            {
                version = vr.InformationalVersion ?? "-";
                branch = vr.BranchName ?? vr.EscapedBranchName ?? "-";
            }

            // Detect tag/detached HEAD; when on a tag we don't have a real branch.
            var currentTag = await git.GetCheckedOutTagAsync(repoPath, cancellationToken);
            if (currentTag != null)
                branch = "-";

            int? defaultBehind = null;
            int? defaultAhead = null;
            bool? hasUpstream = null;
            if (branch != "-")
            {
                var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, cancellationToken);
                var countsTask = git.ProbeCommitCountsAsync(repoPath, branch, defaultRef, cancellationToken);
                var vsDefaultTask = git.GetCommitCountsVsDefaultAsync(repoPath, defaultRef, cancellationToken);
                await Task.WhenAll(countsTask, vsDefaultTask);
                var counts = await countsTask;
                (defaultBehind, defaultAhead, _) = await vsDefaultTask;
                outgoingCommits = counts.Outgoing;
                incomingCommits = counts.Incoming;
                // Upstream comes from the branch's git-configured upstream. Matching the branch name against
                // the remote list, as this used to, called any branch that merely shares a name with a remote
                // ref upstreamed, and said nothing at all when the remote list came back empty.
                if (counts.UpstreamProbed)
                    hasUpstream = counts.HasUpstream;
            }

            string? token = null;
            if (request.RepositoryId > 0)
                token = await tokenProvider.GetTokenForRepositoryAsync(request.RepositoryId, cancellationToken);

            var remoteBranches = token == null
                ? Array.Empty<string>()
                : await git.GetRemoteBranchesAsync(repoPath, token, cancellationToken);
            var localBranches = await git.GetLocalBranchesAsync(repoPath, cancellationToken);

            return new RefreshRepositoryVersionResponse
            {
                Version = version,
                Branch = branch,
                Tag = currentTag,
                OutgoingCommits = outgoingCommits,
                IncomingCommits = incomingCommits,
                GitVersionError = versionError,
                HasUpstream = hasUpstream,
                RemoteBranches = remoteBranches.ToList(),
                LocalBranches = localBranches.ToList(),
                DefaultBranchBehind = defaultBehind,
                DefaultBranchAhead = defaultAhead
            };
        }

        return new RefreshRepositoryVersionResponse { Version = version, Branch = branch, OutgoingCommits = outgoingCommits, IncomingCommits = incomingCommits };
    }
}
