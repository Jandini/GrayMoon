using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Refreshes project and package reference data from .csproj files only. No git operations. A workspace whose
/// capabilities switch project discovery off gets a null project list - not scanned - rather than an empty one,
/// which would read as "this repository has no projects" and prune whatever is persisted.
/// </summary>
public sealed class RefreshRepositoryProjectsCommand(ICsProjFileService csProjFileService) : ICommandHandler<RefreshRepositoryProjectsRequest, RefreshRepositoryProjectsResponse>
{
    public async Task<RefreshRepositoryProjectsResponse> ExecuteAsync(RefreshRepositoryProjectsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var discoverProjects = request.EffectiveCapabilities.ShouldDiscoverProjects
            && !WorkerRepositoryPaths.IsWorkspaceRepository(repositoryName, request.WorkspaceRepositoryName);
        if (!discoverProjects)
            return new RefreshRepositoryProjectsResponse { Projects = null };

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
            return new RefreshRepositoryProjectsResponse { Projects = [] };

        var maxParallel = request.MaxParallelOperations is > 0 ? request.MaxParallelOperations : null;
        var projects = await csProjFileService.FindAsync(repoPath, cancellationToken, maxParallel);
        return new RefreshRepositoryProjectsResponse { Projects = projects };
    }
}
