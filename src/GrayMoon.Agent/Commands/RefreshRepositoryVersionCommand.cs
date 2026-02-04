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

        var workspacePath = git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        var version = "-";
        var branch = "-";
        if (git.DirectoryExists(repoPath))
        {
            var vr = await git.GetVersionAsync(repoPath, cancellationToken);
            if (vr != null)
            {
                version = vr.SemVer ?? vr.FullSemVer ?? "-";
                branch = vr.BranchName ?? vr.EscapedBranchName ?? "-";
            }
        }

        return new RefreshRepositoryVersionResponse { Version = version, Branch = branch };
    }
}
