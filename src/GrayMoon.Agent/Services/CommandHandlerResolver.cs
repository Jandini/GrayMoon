using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Services;

public sealed class CommandHandlerResolver : ICommandHandlerResolver
{
    private readonly IReadOnlyDictionary<string, Func<object, CancellationToken, Task<object?>>> _executors;

    public CommandHandlerResolver(
        ICommandHandler<SyncRepositoryRequest, SyncRepositoryResponse> syncRepository,
        ICommandHandler<RefreshRepositoryVersionRequest, RefreshRepositoryVersionResponse> refreshRepositoryVersion,
        ICommandHandler<EnsureWorkspaceRequest, EnsureWorkspaceResponse> ensureWorkspace,
        ICommandHandler<GetWorkspaceRepositoriesRequest, GetWorkspaceRepositoriesResponse> getWorkspaceRepositories,
        ICommandHandler<GetRepositoryVersionRequest, GetRepositoryVersionResponse> getRepositoryVersion,
        ICommandHandler<GetWorkspaceExistsRequest, GetWorkspaceExistsResponse> getWorkspaceExists)
    {
        _executors = new Dictionary<string, Func<object, CancellationToken, Task<object?>>>(StringComparer.Ordinal)
        {
            ["SyncRepository"] = async (req, ct) => await syncRepository.ExecuteAsync((SyncRepositoryRequest)req, ct),
            ["RefreshRepositoryVersion"] = async (req, ct) => await refreshRepositoryVersion.ExecuteAsync((RefreshRepositoryVersionRequest)req, ct),
            ["EnsureWorkspace"] = async (req, ct) => await ensureWorkspace.ExecuteAsync((EnsureWorkspaceRequest)req, ct),
            ["GetWorkspaceRepositories"] = async (req, ct) => await getWorkspaceRepositories.ExecuteAsync((GetWorkspaceRepositoriesRequest)req, ct),
            ["GetRepositoryVersion"] = async (req, ct) => await getRepositoryVersion.ExecuteAsync((GetRepositoryVersionRequest)req, ct),
            ["GetWorkspaceExists"] = async (req, ct) => await getWorkspaceExists.ExecuteAsync((GetWorkspaceExistsRequest)req, ct),
        };
    }

    public Task<object?> ExecuteAsync(string commandName, object request, CancellationToken cancellationToken = default)
    {
        if (_executors.TryGetValue(commandName, out var executor))
            return executor(request, cancellationToken);
        throw new NotSupportedException($"Unknown command: {commandName}");
    }
}
