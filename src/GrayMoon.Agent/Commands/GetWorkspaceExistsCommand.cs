using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class GetWorkspaceExistsCommand(IGitService git) : ICommandHandler<GetWorkspaceExistsRequest, GetWorkspaceExistsResponse>
{
    public Task<GetWorkspaceExistsResponse> ExecuteAsync(GetWorkspaceExistsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var exists = git.DirectoryExists(path);
        return Task.FromResult(new GetWorkspaceExistsResponse { Exists = exists });
    }
}
