using GrayMoon.Worker.Services;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

public sealed class EnsureWorkspaceCommand() : ICommandHandler<EnsureWorkspaceRequest, EnsureWorkspaceResponse>
{
    public Task<EnsureWorkspaceResponse> ExecuteAsync(EnsureWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        Directory.CreateDirectory(path);
        return Task.FromResult(new EnsureWorkspaceResponse());
    }
}
