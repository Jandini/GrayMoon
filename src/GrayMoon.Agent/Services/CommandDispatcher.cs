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
    ICommandHandler<GetHostInfoRequest, GetHostInfoResponse> getHostInfoCommand,
    ICommandHandler<SyncRepositoryDependenciesRequest, SyncRepositoryDependenciesResponse> syncRepositoryDependenciesCommand,
    ICommandHandler<RefreshRepositoryProjectsRequest, RefreshRepositoryProjectsResponse> refreshRepositoryProjectsCommand,
    ICommandHandler<CommitSyncRepositoryRequest, CommitSyncRepositoryResponse> commitSyncRepositoryCommand,
    ICommandHandler<GetBranchesRequest, GetBranchesResponse> getBranchesCommand,
    ICommandHandler<CheckoutBranchRequest, CheckoutBranchResponse> checkoutBranchCommand,
    ICommandHandler<SyncToDefaultBranchRequest, SyncToDefaultBranchResponse> syncToDefaultBranchCommand,
    ICommandHandler<RefreshBranchesRequest, RefreshBranchesResponse> refreshBranchesCommand,
    ICommandHandler<CreateBranchRequest, CreateBranchResponse> createBranchCommand,
    ICommandHandler<DeleteBranchRequest, DeleteBranchResponse> deleteBranchCommand,
    ICommandHandler<StageAndCommitRequest, StageAndCommitResponse> stageAndCommitCommand,
    ICommandHandler<PushRepositoryRequest, PushRepositoryResponse> pushRepositoryCommand,
    ICommandHandler<SearchFilesRequest, SearchFilesResponse> searchFilesCommand,
    ICommandHandler<UpdateFileVersionsRequest, UpdateFileVersionsResponse> updateFileVersionsCommand,
    ICommandHandler<GetFileContentsRequest, GetFileContentsResponse> getFileContentsCommand,
    ICommandHandler<ValidatePathRequest, ValidatePathResponse> validatePathCommand) : ICommandDispatcher
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
        ["GetHostInfo"] = async (req, ct) => await getHostInfoCommand.ExecuteAsync((GetHostInfoRequest)req, ct),
        ["SyncRepositoryDependencies"] = async (req, ct) => await syncRepositoryDependenciesCommand.ExecuteAsync((SyncRepositoryDependenciesRequest)req, ct),
        ["CommitSyncRepository"] = async (req, ct) => await commitSyncRepositoryCommand.ExecuteAsync((CommitSyncRepositoryRequest)req, ct),
        ["GetBranches"] = async (req, ct) => await getBranchesCommand.ExecuteAsync((GetBranchesRequest)req, ct),
        ["CheckoutBranch"] = async (req, ct) => await checkoutBranchCommand.ExecuteAsync((CheckoutBranchRequest)req, ct),
        ["SyncToDefaultBranch"] = async (req, ct) => await syncToDefaultBranchCommand.ExecuteAsync((SyncToDefaultBranchRequest)req, ct),
        ["RefreshBranches"] = async (req, ct) => await refreshBranchesCommand.ExecuteAsync((RefreshBranchesRequest)req, ct),
        ["CreateBranch"] = async (req, ct) => await createBranchCommand.ExecuteAsync((CreateBranchRequest)req, ct),
        ["DeleteBranch"] = async (req, ct) => await deleteBranchCommand.ExecuteAsync((DeleteBranchRequest)req, ct),
        ["StageAndCommit"] = async (req, ct) => await stageAndCommitCommand.ExecuteAsync((StageAndCommitRequest)req, ct),
        ["PushRepository"] = async (req, ct) => await pushRepositoryCommand.ExecuteAsync((PushRepositoryRequest)req, ct),
        ["SearchFiles"] = async (req, ct) => await searchFilesCommand.ExecuteAsync((SearchFilesRequest)req, ct),
        ["UpdateFileVersions"] = async (req, ct) => await updateFileVersionsCommand.ExecuteAsync((UpdateFileVersionsRequest)req, ct),
        ["GetFileContents"] = async (req, ct) => await getFileContentsCommand.ExecuteAsync((GetFileContentsRequest)req, ct),
        ["ValidatePath"] = async (req, ct) => await validatePathCommand.ExecuteAsync((ValidatePathRequest)req, ct),
    };

    public Task<object?> ExecuteAsync(string commandName, object request, CancellationToken cancellationToken = default)
    {
        if (_executors.TryGetValue(commandName, out var executor))
            return executor(request, cancellationToken);
        throw new NotSupportedException($"Unknown command: {commandName}");
    }
}
