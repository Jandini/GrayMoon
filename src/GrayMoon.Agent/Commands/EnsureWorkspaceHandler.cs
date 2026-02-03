using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class EnsureWorkspaceHandler : ICommandHandler<EnsureWorkspaceRequest, EnsureWorkspaceResponse>
{
    private readonly GitOperations _git;

    public EnsureWorkspaceHandler(GitOperations git)
    {
        _git = git;
    }

    public Task<EnsureWorkspaceResponse> ExecuteAsync(EnsureWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = _git.GetWorkspacePath(workspaceName);
        _git.CreateDirectory(path);
        return Task.FromResult(new EnsureWorkspaceResponse());
    }
}
