using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class GetWorkspaceRepositoriesHandler : ICommandHandler<GetWorkspaceRepositoriesRequest, GetWorkspaceRepositoriesResponse>
{
    private readonly GitOperations _git;

    public GetWorkspaceRepositoriesHandler(GitOperations git)
    {
        _git = git;
    }

    public Task<GetWorkspaceRepositoriesResponse> ExecuteAsync(GetWorkspaceRepositoriesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = _git.GetWorkspacePath(workspaceName);
        var repositories = _git.GetDirectories(path);
        return Task.FromResult(new GetWorkspaceRepositoriesResponse { Repositories = repositories });
    }
}
