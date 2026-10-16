using System.Diagnostics;
using Microsoft.Extensions.Logging;
using GrayMoon.Common.Ai;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;
using GrayMoon.Worker.Services.GitEvidence;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Collects bounded Git evidence for every repository of one AI generation with bounded parallelism (never one task
/// per file). A repository that cannot be compared gets its own error entry; the others still complete, and the App
/// decides whether partial coverage is acceptable (it is not for a pull request).
/// </summary>
public sealed class CollectGitEvidenceCommand(GitEvidenceCollector collector, ILogger<CollectGitEvidenceCommand> logger)
    : ICommandHandler<CollectGitEvidenceRequest, CollectGitEvidenceResponse>
{
    private const int DefaultParallelism = 8;

    public async Task<CollectGitEvidenceResponse> ExecuteAsync(CollectGitEvidenceRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var root = request.WorkspaceRoot ?? throw new ArgumentException("workspaceRoot required");
        var repositories = request.Repositories ?? [];
        var limits = request.Limits ?? new GitEvidenceLimits();
        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(root, workspaceName);
        var started = Stopwatch.GetTimestamp();

        var results = new RepositoryGitEvidence[repositories.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, repositories.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(request.MaxParallelOperations ?? DefaultParallelism, 1, 32),
                CancellationToken = cancellationToken,
            },
            async (index, ct) => results[index] = await CollectOneAsync(workspacePath, request, repositories[index], limits, ct));

        var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation(
            "Git evidence collected. Kind={Kind} Mode={Mode} Repositories={Repositories} Failed={Failed} Files={Files} PatchFiles={PatchFiles} ElapsedMs={ElapsedMs}",
            request.Kind, request.CommitMode, results.Length, results.Count(r => !r.Success),
            results.Sum(r => r.TotalFiles), results.Sum(r => r.Files.Count(f => f.Patch is not null)), elapsed);

        return new CollectGitEvidenceResponse { Repositories = [.. results], ElapsedMs = elapsed };
    }

    private async Task<RepositoryGitEvidence> CollectOneAsync(
        string workspacePath,
        CollectGitEvidenceRequest request,
        GitEvidenceRepositoryScope scope,
        GitEvidenceLimits limits,
        CancellationToken cancellationToken)
    {
        var name = scope.RepositoryName?.Trim();
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0)
            return Failed(scope, GitEvidenceErrorCodes.RepositoryNotFound, "Repository name is not valid.");

        var repositoryPath = WorkerRepositoryPaths.Resolve(workspacePath, name, request.WorkspaceRepositoryName);
        if (!Directory.Exists(repositoryPath) || !WorkerRepositoryPaths.HasGitMetadata(repositoryPath))
            return Failed(scope, GitEvidenceErrorCodes.RepositoryNotFound, "Repository not found.");

        try
        {
            return request.Kind == GitEvidenceKind.PullRequest
                ? await collector.CollectPullRequestAsync(repositoryPath, scope, limits, cancellationToken)
                : await collector.CollectCommitAsync(repositoryPath, scope, request.CommitMode, limits, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // PathUnderRemovalException (the folder is being removed), IO errors and the like: this repository only.
            logger.LogWarning(ex, "Git evidence failed for {Repository}", name);
            return Failed(scope, GitEvidenceErrorCodes.GitFailed, ex.Message);
        }
    }

    private static RepositoryGitEvidence Failed(GitEvidenceRepositoryScope scope, string code, string message) => new()
    {
        RepositoryId = scope.RepositoryId,
        RepositoryName = scope.RepositoryName ?? string.Empty,
        Success = false,
        ErrorCode = code,
        ErrorMessage = message,
    };
}
