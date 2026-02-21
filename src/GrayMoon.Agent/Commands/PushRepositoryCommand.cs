using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

/// <summary>Pushes the current branch to origin and sets upstream (-u) so the branch is upstreamed even when there are no commits to push.</summary>
public sealed class PushRepositoryCommand(IGitService git) : ICommandHandler<PushRepositoryRequest, PushRepositoryResponse>
{
    public async Task<PushRepositoryResponse> ExecuteAsync(PushRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ExecuteCoreAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            return new PushRepositoryResponse
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<PushRepositoryResponse> ExecuteCoreAsync(PushRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var bearerToken = request.BearerToken;

        var workspacePath = git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new PushRepositoryResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        var versionResult = await git.GetVersionAsync(repoPath, cancellationToken);
        if (versionResult == null)
        {
            return new PushRepositoryResponse
            {
                Success = false,
                ErrorMessage = "Could not determine repository version"
            };
        }

        var branch = versionResult.BranchName ?? versionResult.EscapedBranchName;
        if (string.IsNullOrWhiteSpace(branch))
        {
            return new PushRepositoryResponse
            {
                Success = false,
                ErrorMessage = "Could not determine branch name"
            };
        }

        // Push with -u so the branch is upstreamed even when there are no commits to push
        var (success, errorMessage) = await git.PushAsync(repoPath, branch, bearerToken, setTracking: true, ct: cancellationToken);

        return new PushRepositoryResponse
        {
            Success = success,
            ErrorMessage = success ? null : errorMessage
        };
    }
}
