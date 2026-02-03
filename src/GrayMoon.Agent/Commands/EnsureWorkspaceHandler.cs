using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Results;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class EnsureWorkspaceHandler : ICommandHandler<EnsureWorkspaceRequest, EnsureWorkspaceResult>
{
    private readonly GitOperations _git;

    public EnsureWorkspaceHandler(GitOperations git)
    {
        _git = git;
    }

    public Task<EnsureWorkspaceResult> ExecuteAsync(EnsureWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = _git.GetWorkspacePath(workspaceName);
        _git.CreateDirectory(path);
        return Task.FromResult(new EnsureWorkspaceResult());
    }
}
