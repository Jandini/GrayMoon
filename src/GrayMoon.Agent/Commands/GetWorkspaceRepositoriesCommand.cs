using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Commands;

public sealed class GetWorkspaceRepositoriesCommand(IGitService git) : ICommandHandler<GetWorkspaceRepositoriesRequest, GetWorkspaceRepositoriesResponse>
{
    private const int MaxConcurrentRepos = 8;

    public async Task<GetWorkspaceRepositoriesResponse> ExecuteAsync(GetWorkspaceRepositoriesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var path = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repositories = git.GetDirectories(path);

        if (repositories.Length == 0)
        {
            return new GetWorkspaceRepositoriesResponse
            {
                Repositories = [],
                RepositoryInfos = []
            };
        }

        var infos = new WorkspaceRepositoryInfo[repositories.Length];
        using var semaphore = new SemaphoreSlim(MaxConcurrentRepos);

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
                var originUrl = await git.GetRemoteOriginUrlAsync(repoPath, ct);

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
