using System.Diagnostics;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using Microsoft.Extensions.Logging;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class SyncRepositoryDependenciesCommand(ICsProjFileService csProjFileService, ILogger<SyncRepositoryDependenciesCommand> logger) : ICommandHandler<SyncRepositoryDependenciesRequest, SyncRepositoryDependenciesResponse>
{
    public async Task<SyncRepositoryDependenciesResponse> ExecuteAsync(SyncRepositoryDependenciesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var projectUpdates = request.ProjectUpdates ?? [];

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
            return new SyncRepositoryDependenciesResponse { UpdatedCount = 0 };

        // The App is not a trusted source of paths, and these files are rewritten. One bad path refuses the whole
        // request before anything is written, so the caller gets an error instead of a partial update.
        var updates = projectUpdates
            .Where(p => !string.IsNullOrWhiteSpace(p.ProjectPath) && p.PackageUpdates is { Count: > 0 })
            .Select(p =>
            {
                var validation = GitRepositoryPathValidator.Validate(repoPath, p.ProjectPath);
                if (!validation.IsValid || validation.FullPath is null || validation.NormalizedRelativePath is null)
                    throw new ArgumentException($"{validation.ErrorMessage ?? "Invalid path."} Project: {p.ProjectPath}");
                if (RepositoryPathContainment.GetEscapeReason(repoPath, validation.FullPath) is { } escape)
                    throw new ArgumentException($"{escape} Project: {p.ProjectPath}");

                var dict = p.PackageUpdates!
                    .Where(u => !string.IsNullOrWhiteSpace(u.PackageId) && u.NewVersion != null)
                    .ToDictionary(u => u.PackageId!.Trim(), u => u.NewVersion!.Trim(), StringComparer.OrdinalIgnoreCase);
                return (ProjectPath: validation.NormalizedRelativePath, PackageUpdates: (IReadOnlyDictionary<string, string>)dict);
            })
            .Where(t => t.PackageUpdates.Count > 0)
            .ToList();

        var sw = Stopwatch.StartNew();
        var updatedCount = await csProjFileService.UpdatePackageVersionsAsync(repoPath, updates, cancellationToken);
        sw.Stop();
        logger.LogDebug("UpdatePackageVersions completed in {ElapsedMs}ms for {RepoName} ({UpdatedCount} updated)",
            sw.ElapsedMilliseconds, repositoryName, updatedCount);
        return new SyncRepositoryDependenciesResponse { UpdatedCount = updatedCount };
    }
}
