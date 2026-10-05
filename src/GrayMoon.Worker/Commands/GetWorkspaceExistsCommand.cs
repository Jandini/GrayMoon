using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

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
