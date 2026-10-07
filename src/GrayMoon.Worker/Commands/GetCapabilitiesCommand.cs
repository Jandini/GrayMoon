using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

/// <summary>Cheap, process-free advertisement of the Worker features (see <see cref="WorkerFeatures"/>). Kept apart from GetHostInfo, which launches dotnet/git/gitversion.</summary>
public sealed class GetCapabilitiesCommand : ICommandHandler<GetCapabilitiesRequest, GetCapabilitiesResponse>
{
    public Task<GetCapabilitiesResponse> ExecuteAsync(GetCapabilitiesRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new GetCapabilitiesResponse { SupportedFeatures = [WorkerFeatures.WorkspaceRepository] });
}
