using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class RefreshRepositoryVersionCommand(IGitService git) : ICommandHandler<RefreshRepositoryVersionRequest, RefreshRepositoryVersionResponse>
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
                version = vr.SemVer ?? vr.FullSemVer ?? "-";
                branch = vr.BranchName ?? vr.EscapedBranchName ?? "-";
            }
            if (branch != "-")
            {
                var (outgoing, incoming) = await git.GetCommitCountsAsync(repoPath, branch, cancellationToken);
                outgoingCommits = outgoing;
                incomingCommits = incoming;
            }
            return new RefreshRepositoryVersionResponse { Version = version, Branch = branch, OutgoingCommits = outgoingCommits, IncomingCommits = incomingCommits, GitVersionError = versionError };
        }

        return new RefreshRepositoryVersionResponse { Version = version, Branch = branch, OutgoingCommits = outgoingCommits, IncomingCommits = incomingCommits };
    }
}
