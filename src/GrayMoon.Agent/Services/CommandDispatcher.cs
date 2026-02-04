using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Services;

public sealed class CommandDispatcher(
    ICommandHandler<SyncRepositoryRequest, SyncRepositoryResponse> syncRepositoryCommand,
    ICommandHandler<RefreshRepositoryVersionRequest, RefreshRepositoryVersionResponse> refreshRepositoryVersionCommand,
    ICommandHandler<EnsureWorkspaceRequest, EnsureWorkspaceResponse> ensureWorkspaceCommand,
    ICommandHandler<GetWorkspaceRepositoriesRequest, GetWorkspaceRepositoriesResponse> getWorkspaceRepositoriesCommand,
    ICommandHandler<GetRepositoryVersionRequest, GetRepositoryVersionResponse> getRepositoryVersionCommand,
    ICommandHandler<GetWorkspaceExistsRequest, GetWorkspaceExistsResponse> getWorkspaceExistsCommand) : ICommandDispatcher
{
    private readonly IReadOnlyDictionary<string, Func<object, CancellationToken, Task<object?>>> _executors = new Dictionary<string, Func<object, CancellationToken, Task<object?>>>(StringComparer.Ordinal)
    {
        ["SyncRepository"] = async (req, ct) => await syncRepositoryCommand.ExecuteAsync((SyncRepositoryRequest)req, ct),
        ["RefreshRepositoryVersion"] = async (req, ct) => await refreshRepositoryVersionCommand.ExecuteAsync((RefreshRepositoryVersionRequest)req, ct),
        ["EnsureWorkspace"] = async (req, ct) => await ensureWorkspaceCommand.ExecuteAsync((EnsureWorkspaceRequest)req, ct),
        ["GetWorkspaceRepositories"] = async (req, ct) => await getWorkspaceRepositoriesCommand.ExecuteAsync((GetWorkspaceRepositoriesRequest)req, ct),
        ["GetRepositoryVersion"] = async (req, ct) => await getRepositoryVersionCommand.ExecuteAsync((GetRepositoryVersionRequest)req, ct),
        ["GetWorkspaceExists"] = async (req, ct) => await getWorkspaceExistsCommand.ExecuteAsync((GetWorkspaceExistsRequest)req, ct),
    };

    public Task<object?> ExecuteAsync(string commandName, object request, CancellationToken cancellationToken = default)
    {
        if (_executors.TryGetValue(commandName, out var executor))
            return executor(request, cancellationToken);
        throw new NotSupportedException($"Unknown command: {commandName}");
    }
}
