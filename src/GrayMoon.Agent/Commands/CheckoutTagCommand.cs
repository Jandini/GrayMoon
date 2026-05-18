using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class CheckoutTagCommand(IGitService git) : ICommandHandler<CheckoutTagRequest, CheckoutTagResponse>
{
    public async Task<CheckoutTagResponse> ExecuteAsync(CheckoutTagRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var tagName = request.TagName ?? throw new ArgumentException("tagName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new CheckoutTagResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        var (success, errorMessage) = await git.CheckoutTagAsync(repoPath, tagName, cancellationToken);
        if (!success)
        {
            return new CheckoutTagResponse
            {
                Success = false,
                ErrorMessage = errorMessage ?? "Failed to checkout tag"
            };
        }

        // The checkout hook will run and send a SyncCommand with the new version and tag state.
        return new CheckoutTagResponse
        {
            Success = true,
            CurrentTag = tagName
        };
    }
}
