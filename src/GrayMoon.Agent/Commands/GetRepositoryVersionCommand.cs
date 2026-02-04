using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class GetRepositoryVersionCommand : ICommandHandler<GetRepositoryVersionRequest, GetRepositoryVersionResponse>
{
    private readonly IGitService _git;

    public GetRepositoryVersionCommand(IGitService git)
    {
        _git = git;
    }

    public async Task<GetRepositoryVersionResponse> ExecuteAsync(GetRepositoryVersionRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = _git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);
        var exists = _git.DirectoryExists(repoPath);

        string? version = null;
        string? branch = null;
        if (exists)
        {
            var vr = await _git.GetVersionAsync(repoPath, cancellationToken);
            if (vr != null)
            {
                version = vr.SemVer ?? vr.FullSemVer;
                branch = vr.BranchName ?? vr.EscapedBranchName;
            }
        }

        return new GetRepositoryVersionResponse { Exists = exists, Version = version, Branch = branch };
    }
}
