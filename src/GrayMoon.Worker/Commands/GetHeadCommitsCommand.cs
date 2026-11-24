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
        var totalStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var slowestMs = 0L;
        string? slowestRepo = null;
        // Summed over all repositories, per step, so the log shows what one git call costs on this machine
        // (2026-11-25: 36 to 60 s for 40 repositories with nothing else running on the Worker).
        long headMs = 0, branchMs = 0, tagMs = 0, collisionMs = 0;
        await Task.WhenAll(names.Select(async repoName =>
        {
            await semaphore.WaitAsync(cancellationToken);
            var repoStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repoName, request.WorkspaceRepositoryName);
                var stepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var sha = await reader.GetHeadCommitAsync(repoPath, cancellationToken);
                Interlocked.Add(ref headMs, ElapsedMs(ref stepStartedAt));
                if (!string.IsNullOrWhiteSpace(sha))
                    commits[repoName] = sha;
                else
                    logger.LogWarning(
                        "GetHeadCommits: could not resolve HEAD for repository {RepositoryName} under {WorkspacePath} (missing, unborn, or git failed).",
                        repoName, workspacePath);

                var branch = await reader.GetCurrentBranchNameAsync(repoPath, cancellationToken);
                Interlocked.Add(ref branchMs, ElapsedMs(ref stepStartedAt));
                if (!string.IsNullOrWhiteSpace(branch))
                    branches[repoName] = branch;

                var tag = await reader.GetCheckedOutTagAsync(repoPath, cancellationToken);
                Interlocked.Add(ref tagMs, ElapsedMs(ref stepStartedAt));
                if (tag != null)
                    tags[repoName] = tag;

                if (collisionBranch != null && tag == null)
                {
                    var refs = await reader.FindBranchCollisionsAsync(repoPath, collisionBranch, cancellationToken);
                    if (refs.Count > 0)
                        collisions[repoName] = refs.ToList();
                    Interlocked.Add(ref collisionMs, ElapsedMs(ref stepStartedAt));
                }
            }
            finally
            {
                var repoMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(repoStartedAt).TotalMilliseconds;
                lock (semaphore)
                {
                    if (repoMs > slowestMs)
                    {
                        slowestMs = repoMs;
                        slowestRepo = repoName;
                    }
                }

                semaphore.Release();
            }
        }));

        logger.LogInformation(
            "GetHeadCommits timing. Repositories={Count} MaxConcurrent={MaxConcurrent} TotalMs={TotalMs} SlowestRepository={SlowestRepository} SlowestMs={SlowestMs} "
            + "SumHeadMs={SumHeadMs} SumBranchMs={SumBranchMs} SumTagMs={SumTagMs} SumCollisionMs={SumCollisionMs}",
            names.Count, DefaultMaxConcurrent, (long)System.Diagnostics.Stopwatch.GetElapsedTime(totalStartedAt).TotalMilliseconds, slowestRepo, slowestMs,
            headMs, branchMs, tagMs, collisionMs);

        return new GetHeadCommitsResponse
        {
            Commits = new Dictionary<string, string>(commits, StringComparer.OrdinalIgnoreCase),
            Branches = new Dictionary<string, string>(branches, StringComparer.OrdinalIgnoreCase),
            Tags = new Dictionary<string, string>(tags, StringComparer.OrdinalIgnoreCase),
            BranchCollisions = new Dictionary<string, List<string>>(collisions, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Milliseconds since <paramref name="startedAt"/>, which is then moved to now for the next step.</summary>
    private static long ElapsedMs(ref long startedAt)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt, now).TotalMilliseconds;
        startedAt = now;
        return elapsed;
    }
}
