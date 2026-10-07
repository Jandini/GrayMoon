using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class GetGitVersionAtDefaultTipCommand(IGitRepositoryReader reader, IRepositoryVersionProvider versionProvider)
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

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);
        if (!Directory.Exists(repoPath))
        {
            return new GetGitVersionAtDefaultTipResponse
            {
                Success = false,
                ErrorMessage = "Repository not found."
            };
        }

        var defaultBranch = await reader.GetDefaultBranchNameAsync(repoPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(defaultBranch))
        {
            return new GetGitVersionAtDefaultTipResponse
            {
                Success = false,
                ErrorMessage = "Could not resolve default branch."
            };
        }

        var sha = await reader.RevParseAsync(repoPath, $"origin/{defaultBranch}", cancellationToken);
        if (string.IsNullOrWhiteSpace(sha))
        {
            return new GetGitVersionAtDefaultTipResponse
            {
                Success = false,
                ErrorMessage = $"Could not resolve origin/{defaultBranch}. Fetch first.",
                DefaultBranch = defaultBranch
            };
        }

        var versionResult = await versionProvider.GetVersionAsync(
            repoPath,
            new RepositoryVersionOptions { NonNormalize = true, CommitSha = sha },
            cancellationToken);
        var error = versionResult.Error;
        var version = versionResult.InformationalVersion;
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
