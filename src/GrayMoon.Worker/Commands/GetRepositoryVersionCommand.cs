using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class GetRepositoryVersionCommand(
    IGitRepositoryReader reader,
    IRepositoryVersionProviderFactory versionProviderFactory) : ICommandHandler<GetRepositoryVersionRequest, GetRepositoryVersionResponse>
{
    public async Task<GetRepositoryVersionResponse> ExecuteAsync(GetRepositoryVersionRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);
        var exists = Directory.Exists(repoPath);

        string? version = null;
        string? branch = null;
        bool? versionProbed = null;
        if (exists)
        {
            var versionResult = await versionProviderFactory
                .Create(request.EffectiveCapabilities)
                .GetVersionAsync(repoPath, RepositoryVersionOptions.Default, cancellationToken);
            version = versionResult.InformationalVersion;
            versionProbed = versionResult.Probed;
            // A version provider that failed, or that is switched off, leaves the version unresolved; it must
            // not cost the repository its branch.
            branch = await reader.ResolveBranchAsync(versionResult.Result, repoPath, cancellationToken);
        }

        return new GetRepositoryVersionResponse
        {
            Exists = exists,
            Version = version,
            Branch = branch,
            VersionProbed = versionProbed,
        };
    }
}
