using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class EnsureWorkspaceCommand(IGitService git) : ICommandHandler<EnsureWorkspaceRequest, EnsureWorkspaceResponse>
{
    public Task<EnsureWorkspaceResponse> ExecuteAsync(EnsureWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        git.CreateDirectory(path);
        return Task.FromResult(new EnsureWorkspaceResponse());
    }
}
