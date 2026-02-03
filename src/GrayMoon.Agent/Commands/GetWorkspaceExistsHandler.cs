using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Results;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class GetWorkspaceExistsHandler : ICommandHandler<GetWorkspaceExistsRequest, GetWorkspaceExistsResult>
{
    private readonly GitOperations _git;

    public GetWorkspaceExistsHandler(GitOperations git)
    {
        _git = git;
    }

    public Task<GetWorkspaceExistsResult> ExecuteAsync(GetWorkspaceExistsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = _git.GetWorkspacePath(workspaceName);
        var exists = _git.DirectoryExists(path);
        return Task.FromResult(new GetWorkspaceExistsResult { Exists = exists });
    }
}
