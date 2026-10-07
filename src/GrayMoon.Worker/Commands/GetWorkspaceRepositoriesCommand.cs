using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class GetWorkspaceRepositoriesCommand(IGitRepositoryReader reader) : ICommandHandler<GetWorkspaceRepositoriesRequest, GetWorkspaceRepositoriesResponse>
{
    private const int DefaultMaxConcurrentRepos = 8;

    public async Task<GetWorkspaceRepositoriesResponse> ExecuteAsync(GetWorkspaceRepositoriesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repositories = WorkerRepositoryPaths.GetDirectoryNames(path)
            .Where(name => WorkerRepositoryPaths.HasGitMetadata(Path.Combine(path, name)))
            .ToArray();

        if (repositories.Length == 0)
        {
            return new GetWorkspaceRepositoriesResponse
            {
                Repositories = [],
                RepositoryInfos = []
            };
        }

        var maxConcurrent = request.MaxParallelOperations is > 0 ? request.MaxParallelOperations.Value : DefaultMaxConcurrentRepos;
        var infos = new WorkspaceRepositoryInfo[repositories.Length];
        using var semaphore = new SemaphoreSlim(maxConcurrent);

        var tasks = repositories
            .Select((name, index) => FetchInfoAsync(index, name, path, infos, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks);

        return new GetWorkspaceRepositoriesResponse
        {
            Repositories = repositories,
            RepositoryInfos = infos
        };

        async Task FetchInfoAsync(int index, string name, string workspacePath, WorkspaceRepositoryInfo[] target, CancellationToken ct)
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var repoPath = Path.Combine(workspacePath, name);
                var originUrl = await reader.GetRemoteOriginUrlAsync(repoPath, ct);

                target[index] = new WorkspaceRepositoryInfo
                {
                    Name = name,
                    OriginUrl = originUrl
                };
            }
            finally
            {
                semaphore.Release();
            }
        }
    }
}
