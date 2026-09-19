using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using Microsoft.Extensions.Logging;
namespace GrayMoon.Agent.Commands;
/// <summary>Batch <c>git rev-parse HEAD</c> for the requested repository names under a workspace.</summary>
public sealed class GetHeadCommitsCommand(IGitService git, ILogger<GetHeadCommitsCommand> logger)
    : ICommandHandler<GetHeadCommitsRequest, GetHeadCommitsResponse>
{
    private const int DefaultMaxConcurrent = 8;
    public async Task<GetHeadCommitsResponse> ExecuteAsync(GetHeadCommitsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var names = request.RepositoryNames?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (names.Count == 0)
            return new GetHeadCommitsResponse { Commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var commits = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var semaphore = new SemaphoreSlim(DefaultMaxConcurrent);
        await Task.WhenAll(names.Select(async repoName =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var repoPath = Path.Combine(workspacePath, repoName);
                var sha = await git.GetHeadCommitAsync(repoPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(sha))
                    commits[repoName] = sha;
                else
                    logger.LogWarning(
                        "GetHeadCommits: could not resolve HEAD for repository {RepositoryName} under {WorkspacePath} (missing, unborn, or git failed).",
                        repoName, workspacePath);
            }
            finally
            {
                semaphore.Release();
            }
        }));
        return new GetHeadCommitsResponse
        {
            Commits = new Dictionary<string, string>(commits, StringComparer.OrdinalIgnoreCase)
        };
    }
}
