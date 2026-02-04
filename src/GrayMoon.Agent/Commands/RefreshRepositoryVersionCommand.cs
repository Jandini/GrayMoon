using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class RefreshRepositoryVersionCommand : ICommandHandler<RefreshRepositoryVersionRequest, RefreshRepositoryVersionResponse>
{
    private readonly IGitService _git;

    public RefreshRepositoryVersionCommand(IGitService git)
    {
        _git = git;
    }

    public async Task<RefreshRepositoryVersionResponse> ExecuteAsync(RefreshRepositoryVersionRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = _git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        var version = "-";
        var branch = "-";
        if (_git.DirectoryExists(repoPath))
        {
            var vr = await _git.GetVersionAsync(repoPath, cancellationToken);
            if (vr != null)
            {
                version = vr.SemVer ?? vr.FullSemVer ?? "-";
                branch = vr.BranchName ?? vr.EscapedBranchName ?? "-";
            }
        }

        return new RefreshRepositoryVersionResponse { Version = version, Branch = branch };
    }
}
