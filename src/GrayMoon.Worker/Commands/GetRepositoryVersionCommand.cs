using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class GetRepositoryVersionCommand(IGitService git) : ICommandHandler<GetRepositoryVersionRequest, GetRepositoryVersionResponse>
{
    public async Task<GetRepositoryVersionResponse> ExecuteAsync(GetRepositoryVersionRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);
        var exists = git.DirectoryExists(repoPath);

        string? version = null;
        string? branch = null;
        if (exists)
        {
            var (vr, _) = await git.GetVersionAsync(repoPath, cancellationToken);
            version = vr?.InformationalVersion;
            // A GitVersion failure leaves the version unresolved; it must not cost the repository its branch.
            branch = await git.ResolveBranchAsync(vr, repoPath, cancellationToken);
        }

        return new GetRepositoryVersionResponse { Exists = exists, Version = version, Branch = branch };
    }
}
