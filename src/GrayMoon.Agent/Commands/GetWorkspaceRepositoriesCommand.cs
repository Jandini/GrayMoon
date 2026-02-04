using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class GetWorkspaceRepositoriesCommand(IGitService git) : ICommandHandler<GetWorkspaceRepositoriesRequest, GetWorkspaceRepositoriesResponse>
{
    public Task<GetWorkspaceRepositoriesResponse> ExecuteAsync(GetWorkspaceRepositoriesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = git.GetWorkspacePath(workspaceName);
        var repositories = git.GetDirectories(path);
        return Task.FromResult(new GetWorkspaceRepositoriesResponse { Repositories = repositories });
    }
}
