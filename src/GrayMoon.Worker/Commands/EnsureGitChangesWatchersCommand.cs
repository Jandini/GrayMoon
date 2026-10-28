using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;
using GrayMoon.Worker.Services.GitChanges;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Renews (or creates, or restarts) the file watchers of a batch of repositories without running git. A watcher
/// that is created or recovered queues its own refresh, so changes made while nothing watched are not lost.
/// Cost is independent of how long a status scan takes, which is why a renewal cannot be starved by scans.
/// </summary>
public sealed class EnsureGitChangesWatchersCommand(
    GitChangesRepositoryRegistry registry,
    GitRepositoryWatcherManager watcherManager,
    ILogger<EnsureGitChangesWatchersCommand> logger) : ICommandHandler<EnsureGitChangesWatchersRequest, EnsureGitChangesWatchersResponse>
{
    /// <summary>Upper bound for one batch; a larger request is answered with partial results, never an error.</summary>
    internal const int MaxBatchSize = 500;

    private static readonly string WorkerInstanceId = Guid.NewGuid().ToString("N");

    public Task<EnsureGitChangesWatchersResponse> ExecuteAsync(EnsureGitChangesWatchersRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);

        var response = new EnsureGitChangesWatchersResponse
        {
            WorkerInstanceId = WorkerInstanceId,
            EffectiveGraceSeconds = (int)watcherManager.IdleGrace.TotalSeconds,
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>();

        foreach (var repository in request.Repositories.Take(MaxBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = EnsureOne(request, workspacePath, repository, seen);
            counts[status] = counts.GetValueOrDefault(status) + 1;
            response.Results.Add(new EnsureGitChangesWatchersResult { RepositoryId = repository.RepositoryId, Status = status });
        }

        if (request.Repositories.Count > MaxBatchSize)
        {
            logger.LogWarning(
                "[GCDiag] EnsureGitChangesWatchers: batch of {Count} exceeds {Max}; the rest were not renewed",
                request.Repositories.Count, MaxBatchSize);
        }

        logger.LogInformation(
            "[GCDiag] EnsureGitChangesWatchers done: workspace={WorkspaceId} repos={Count} {Counts} elapsedMs={ElapsedMs} watcherEntries={Entries} healthy={Healthy}",
            request.WorkspaceId, response.Results.Count,
            string.Join(' ', counts.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}")),
            (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            watcherManager.ActiveWatcherCount, watcherManager.HealthyWatcherCount);
        return Task.FromResult(response);
    }

    private string EnsureOne(
        EnsureGitChangesWatchersRequest request,
        string workspacePath,
        EnsureGitChangesWatchersRepository repository,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(repository.RepositoryName)
            || Path.IsPathRooted(repository.RepositoryName)
            || repository.RepositoryName.IndexOfAny(['/', '\\']) >= 0
            || repository.RepositoryName is "." or "..")
        {
            return "Invalid";
        }

        string repoPath;
        try
        {
            repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repository.RepositoryName, request.WorkspaceRepositoryName);
        }
        catch (ArgumentException)
        {
            return "Invalid";
        }

        if (!Directory.Exists(repoPath))
        {
            return "Missing";
        }

        // The same folder listed twice is renewed once; the second entry reports the same outcome as a plain renewal.
        if (!seen.Add(GitChangesSnapshotCache.NormalizeKey(repoPath)))
        {
            return "Renewed";
        }

        registry.Register(repoPath, request.WorkspaceId, repository.RepositoryId);
        return watcherManager.Touch(repoPath).ToString();
    }
}
