using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

/// <summary>Refreshes project and package reference data from .csproj files only. No git operations.</summary>
public sealed class RefreshRepositoryProjectsCommand(IGitService git, ICsProjFileService csProjFileService) : ICommandHandler<RefreshRepositoryProjectsRequest, RefreshRepositoryProjectsResponse>
{
    public async Task<RefreshRepositoryProjectsResponse> ExecuteAsync(RefreshRepositoryProjectsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
            return new RefreshRepositoryProjectsResponse { Projects = [] };

        var projects = await csProjFileService.FindAsync(repoPath, cancellationToken);
        return new RefreshRepositoryProjectsResponse { Projects = projects };
    }
}
