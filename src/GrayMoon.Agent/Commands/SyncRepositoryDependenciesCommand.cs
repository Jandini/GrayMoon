using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class SyncRepositoryDependenciesCommand(IGitService git, ICsProjFileService csProjFileService) : ICommandHandler<SyncRepositoryDependenciesRequest, SyncRepositoryDependenciesResponse>
{
    public async Task<SyncRepositoryDependenciesResponse> ExecuteAsync(SyncRepositoryDependenciesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var projectUpdates = request.ProjectUpdates ?? [];

        var workspacePath = git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
            return new SyncRepositoryDependenciesResponse { UpdatedCount = 0 };

        var updates = projectUpdates
            .Where(p => !string.IsNullOrWhiteSpace(p.ProjectPath) && p.PackageUpdates is { Count: > 0 })
            .Select(p =>
            {
                var dict = p.PackageUpdates!
                    .Where(u => !string.IsNullOrWhiteSpace(u.PackageId) && u.NewVersion != null)
                    .ToDictionary(u => u.PackageId!.Trim(), u => u.NewVersion!.Trim(), StringComparer.OrdinalIgnoreCase);
                return (ProjectPath: p.ProjectPath!.Trim(), PackageUpdates: (IReadOnlyDictionary<string, string>)dict);
            })
            .Where(t => t.PackageUpdates.Count > 0)
            .ToList();

        var updatedCount = await csProjFileService.UpdatePackageVersionsAsync(repoPath, updates, cancellationToken);
        return new SyncRepositoryDependenciesResponse { UpdatedCount = updatedCount };
    }
}
