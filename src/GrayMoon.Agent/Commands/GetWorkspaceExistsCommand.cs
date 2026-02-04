using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class GetWorkspaceExistsCommand : ICommandHandler<GetWorkspaceExistsRequest, GetWorkspaceExistsResponse>
{
    private readonly IGitService _git;

    public GetWorkspaceExistsCommand(IGitService git)
    {
        _git = git;
    }

    public Task<GetWorkspaceExistsResponse> ExecuteAsync(GetWorkspaceExistsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = _git.GetWorkspacePath(workspaceName);
        var exists = _git.DirectoryExists(path);
        return Task.FromResult(new GetWorkspaceExistsResponse { Exists = exists });
    }
}
