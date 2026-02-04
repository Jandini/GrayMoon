using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class SyncRepositoryCommand(IGitService git, ICsProjFileService csProjFileService) : ICommandHandler<SyncRepositoryRequest, SyncRepositoryResponse>
{
    public async Task<SyncRepositoryResponse> ExecuteAsync(SyncRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryId = request.RepositoryId;
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var cloneUrl = request.CloneUrl;
        var bearerToken = request.BearerToken;
        var workspaceId = request.WorkspaceId;

        var workspacePath = git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);
        var wasCloned = false;

        git.CreateDirectory(workspacePath);

        if (!git.DirectoryExists(repoPath) && !string.IsNullOrWhiteSpace(cloneUrl))
        {
            var ok = await git.CloneAsync(workspacePath, cloneUrl, bearerToken, cancellationToken);
            wasCloned = ok;
            if (ok)
                await git.AddSafeDirectoryAsync(repoPath, cancellationToken);
        }

        var version = "-";
        var branch = "-";
        var projects = 0;
        if (git.DirectoryExists(repoPath))
        {
            var vr = await git.GetVersionAsync(repoPath, cancellationToken);
            if (vr != null)
            {
                version = vr.SemVer ?? vr.FullSemVer ?? "-";
                branch = vr.BranchName ?? vr.EscapedBranchName ?? "-";
                if (version != "-" && branch != "-")
                    git.WriteSyncHooks(repoPath, workspaceId, repositoryId);
            }
            projects = await csProjFileService.FindAsync(repoPath, cancellationToken);
        }

        return new SyncRepositoryResponse { Version = version, Branch = branch, WasCloned = wasCloned, Projects = projects };
    }
}
