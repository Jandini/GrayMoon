using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class ListGitWorktreesCommand(IGitService git)
    : ICommandHandler<ListGitWorktreesRequest, ListGitWorktreesResponse>
{
    public async Task<ListGitWorktreesResponse> ExecuteAsync(ListGitWorktreesRequest request, CancellationToken cancellationToken = default)
    {
        var mainPath = request.MainRepositoryPath ?? throw new ArgumentException("mainRepositoryPath required");

        var (success, worktrees, errorCode, errorMessage) = await git.ListWorktreesAsync(mainPath, cancellationToken);
        return new ListGitWorktreesResponse
        {
            Success = success,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Worktrees = success ? worktrees.ToList() : null,
        };
    }
}
