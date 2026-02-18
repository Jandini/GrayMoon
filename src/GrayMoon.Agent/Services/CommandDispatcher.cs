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
    ICommandHandler<GetWorkspaceExistsRequest, GetWorkspaceExistsResponse> getWorkspaceExistsCommand,
    ICommandHandler<GetWorkspaceRootRequest, GetWorkspaceRootResponse> getWorkspaceRootCommand,
    ICommandHandler<GetHostInfoRequest, GetHostInfoResponse> getHostInfoCommand,
    ICommandHandler<SyncRepositoryDependenciesRequest, SyncRepositoryDependenciesResponse> syncRepositoryDependenciesCommand,
    ICommandHandler<RefreshRepositoryProjectsRequest, RefreshRepositoryProjectsResponse> refreshRepositoryProjectsCommand,
    ICommandHandler<PullPushRepositoryRequest, PullPushRepositoryResponse> pullPushRepositoryCommand,
    ICommandHandler<GetBranchesRequest, GetBranchesResponse> getBranchesCommand,
    ICommandHandler<CheckoutBranchRequest, CheckoutBranchResponse> checkoutBranchCommand,
    ICommandHandler<SyncToDefaultBranchRequest, SyncToDefaultBranchResponse> syncToDefaultBranchCommand,
    ICommandHandler<RefreshBranchesRequest, RefreshBranchesResponse> refreshBranchesCommand) : ICommandDispatcher
{
    private readonly IReadOnlyDictionary<string, Func<object, CancellationToken, Task<object?>>> _executors = new Dictionary<string, Func<object, CancellationToken, Task<object?>>>(StringComparer.Ordinal)
    {
        ["SyncRepository"] = async (req, ct) => await syncRepositoryCommand.ExecuteAsync((SyncRepositoryRequest)req, ct),
        ["RefreshRepositoryVersion"] = async (req, ct) => await refreshRepositoryVersionCommand.ExecuteAsync((RefreshRepositoryVersionRequest)req, ct),
        ["RefreshRepositoryProjects"] = async (req, ct) => await refreshRepositoryProjectsCommand.ExecuteAsync((RefreshRepositoryProjectsRequest)req, ct),
        ["EnsureWorkspace"] = async (req, ct) => await ensureWorkspaceCommand.ExecuteAsync((EnsureWorkspaceRequest)req, ct),
        ["GetWorkspaceRepositories"] = async (req, ct) => await getWorkspaceRepositoriesCommand.ExecuteAsync((GetWorkspaceRepositoriesRequest)req, ct),
        ["GetRepositoryVersion"] = async (req, ct) => await getRepositoryVersionCommand.ExecuteAsync((GetRepositoryVersionRequest)req, ct),
        ["GetWorkspaceExists"] = async (req, ct) => await getWorkspaceExistsCommand.ExecuteAsync((GetWorkspaceExistsRequest)req, ct),
        ["GetWorkspaceRoot"] = async (req, ct) => await getWorkspaceRootCommand.ExecuteAsync((GetWorkspaceRootRequest)req, ct),
        ["GetHostInfo"] = async (req, ct) => await getHostInfoCommand.ExecuteAsync((GetHostInfoRequest)req, ct),
        ["SyncRepositoryDependencies"] = async (req, ct) => await syncRepositoryDependenciesCommand.ExecuteAsync((SyncRepositoryDependenciesRequest)req, ct),
        ["PullPushRepository"] = async (req, ct) => await pullPushRepositoryCommand.ExecuteAsync((PullPushRepositoryRequest)req, ct),
        ["GetBranches"] = async (req, ct) => await getBranchesCommand.ExecuteAsync((GetBranchesRequest)req, ct),
        ["CheckoutBranch"] = async (req, ct) => await checkoutBranchCommand.ExecuteAsync((CheckoutBranchRequest)req, ct),
        ["SyncToDefaultBranch"] = async (req, ct) => await syncToDefaultBranchCommand.ExecuteAsync((SyncToDefaultBranchRequest)req, ct),
        ["RefreshBranches"] = async (req, ct) => await refreshBranchesCommand.ExecuteAsync((RefreshBranchesRequest)req, ct),
    };

    public Task<object?> ExecuteAsync(string commandName, object request, CancellationToken cancellationToken = default)
    {
        if (_executors.TryGetValue(commandName, out var executor))
            return executor(request, cancellationToken);
        throw new NotSupportedException($"Unknown command: {commandName}");
    }
}
