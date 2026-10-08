using GrayMoon.Worker.Services;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

public sealed class GetWorkspaceExistsCommand() : ICommandHandler<GetWorkspaceExistsRequest, GetWorkspaceExistsResponse>
{
    public Task<GetWorkspaceExistsResponse> ExecuteAsync(GetWorkspaceExistsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var exists = Directory.Exists(path);
        return Task.FromResult(new GetWorkspaceExistsResponse
        {
            Exists = exists,
            IsEmpty = exists ? !Directory.EnumerateFileSystemEntries(path).Any() : null,
        });
    }
}
