using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class SyncRepositoryHandler : ICommandHandler<SyncRepositoryRequest, SyncRepositoryResponse>
{
    private readonly GitOperations _git;

    public SyncRepositoryHandler(GitOperations git)
    {
        _git = git;
    }

    public async Task<SyncRepositoryResponse> ExecuteAsync(SyncRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryId = request.RepositoryId;
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var cloneUrl = request.CloneUrl;
        var bearerToken = request.BearerToken;
        var workspaceId = request.WorkspaceId;

        var workspacePath = _git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);
        var wasCloned = false;

        _git.CreateDirectory(workspacePath);

        if (!_git.DirectoryExists(repoPath) && !string.IsNullOrWhiteSpace(cloneUrl))
        {
            var ok = await _git.CloneAsync(workspacePath, cloneUrl, bearerToken, cancellationToken);
            wasCloned = ok;
            if (ok)
                await _git.AddSafeDirectoryAsync(repoPath, cancellationToken);
        }

        var version = "-";
        var branch = "-";
        if (_git.DirectoryExists(repoPath))
        {
            var vr = await _git.GetVersionAsync(repoPath, cancellationToken);
            if (vr != null)
            {
                version = vr.SemVer ?? vr.FullSemVer ?? "-";
                branch = vr.BranchName ?? vr.EscapedBranchName ?? "-";
                if (version != "-" && branch != "-")
                    _git.WriteSyncHooks(repoPath, workspaceId, repositoryId);
            }
        }

        return new SyncRepositoryResponse { Version = version, Branch = branch, WasCloned = wasCloned };
    }
}
