using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class GetWorkspaceRootCommand(IGitService git) : ICommandHandler<GetWorkspaceRootRequest, GetWorkspaceRootResponse>
{
    public Task<GetWorkspaceRootResponse> ExecuteAsync(GetWorkspaceRootRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new GetWorkspaceRootResponse { WorkspaceRoot = git.WorkspaceRoot });
    }
}
