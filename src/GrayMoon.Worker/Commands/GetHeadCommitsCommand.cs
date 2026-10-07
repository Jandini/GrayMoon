using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using Microsoft.Extensions.Logging;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Batch <c>git rev-parse HEAD</c>, <c>git branch --show-current</c> and checked-out tag for the requested repository names under a workspace.
/// When <see cref="GetHeadCommitsRequest.CollisionBranchName"/> is set, also reports existing local/remote-tracking refs with that name
/// for repositories that are not on a tag.
/// </summary>
public sealed class GetHeadCommitsCommand(IGitRepositoryReader reader, ILogger<GetHeadCommitsCommand> logger)
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
        {
            return new GetHeadCommitsResponse
            {
                Commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                BranchCollisions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
            };
        }

        var collisionBranch = string.IsNullOrWhiteSpace(request.CollisionBranchName) ? null : request.CollisionBranchName.Trim();
        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var commits = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var branches = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tags = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var collisions = new System.Collections.Concurrent.ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var semaphore = new SemaphoreSlim(DefaultMaxConcurrent);
        await Task.WhenAll(names.Select(async repoName =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repoName, request.WorkspaceRepositoryName);
                var sha = await reader.GetHeadCommitAsync(repoPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(sha))
                    commits[repoName] = sha;
                else
                    logger.LogWarning(
                        "GetHeadCommits: could not resolve HEAD for repository {RepositoryName} under {WorkspacePath} (missing, unborn, or git failed).",
                        repoName, workspacePath);

                var branch = await reader.GetCurrentBranchNameAsync(repoPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(branch))
                    branches[repoName] = branch;

                var tag = await reader.GetCheckedOutTagAsync(repoPath, cancellationToken);
                if (tag != null)
                    tags[repoName] = tag;

                if (collisionBranch != null && tag == null)
                {
                    var refs = await reader.FindBranchCollisionsAsync(repoPath, collisionBranch, cancellationToken);
                    if (refs.Count > 0)
                        collisions[repoName] = refs.ToList();
                }
            }
            finally
            {
                semaphore.Release();
            }
        }));

        return new GetHeadCommitsResponse
        {
            Commits = new Dictionary<string, string>(commits, StringComparer.OrdinalIgnoreCase),
            Branches = new Dictionary<string, string>(branches, StringComparer.OrdinalIgnoreCase),
            Tags = new Dictionary<string, string>(tags, StringComparer.OrdinalIgnoreCase),
            BranchCollisions = new Dictionary<string, List<string>>(collisions, StringComparer.OrdinalIgnoreCase),
        };
    }
}
