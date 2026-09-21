using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class GetGitVersionAtDefaultTipCommand(IGitService git)
    : ICommandHandler<GetGitVersionAtDefaultTipRequest, GetGitVersionAtDefaultTipResponse>
{
    public async Task<GetGitVersionAtDefaultTipResponse> ExecuteAsync(
        GetGitVersionAtDefaultTipRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
            throw new ArgumentException("workspaceRoot required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);
        if (!git.DirectoryExists(repoPath))
        {
            return new GetGitVersionAtDefaultTipResponse
            {
                Success = false,
                ErrorMessage = "Repository not found."
            };
        }

        var defaultBranch = await git.GetDefaultBranchNameAsync(repoPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(defaultBranch))
        {
            return new GetGitVersionAtDefaultTipResponse
            {
                Success = false,
                ErrorMessage = "Could not resolve default branch."
            };
        }

        var sha = await git.RevParseAsync(repoPath, $"origin/{defaultBranch}", cancellationToken);
        if (string.IsNullOrWhiteSpace(sha))
        {
            return new GetGitVersionAtDefaultTipResponse
            {
                Success = false,
                ErrorMessage = $"Could not resolve origin/{defaultBranch}. Fetch first.",
                DefaultBranch = defaultBranch
            };
        }

        var (vr, error) = await git.GetVersionAsync(repoPath, nonNormalize: true, commitSha: sha, cancellationToken);
        var version = vr?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            return new GetGitVersionAtDefaultTipResponse
            {
                Success = false,
                ErrorMessage = error ?? "GitVersion returned no version for default tip.",
                CommitSha = sha,
                DefaultBranch = defaultBranch
            };
        }

        return new GetGitVersionAtDefaultTipResponse
        {
            Success = true,
            Version = version,
            CommitSha = sha,
            DefaultBranch = defaultBranch
        };
    }
}
