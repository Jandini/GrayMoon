using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Results;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class GetWorkspaceRepositoriesHandler : ICommandHandler<GetWorkspaceRepositoriesRequest, GetWorkspaceRepositoriesResult>
{
    private readonly GitOperations _git;

    public GetWorkspaceRepositoriesHandler(GitOperations git)
    {
        _git = git;
    }

    public Task<GetWorkspaceRepositoriesResult> ExecuteAsync(GetWorkspaceRepositoriesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = _git.GetWorkspacePath(workspaceName);
        var repositories = _git.GetDirectories(path);
        return Task.FromResult(new GetWorkspaceRepositoriesResult { Repositories = repositories });
    }
}
