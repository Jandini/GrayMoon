using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

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
