using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class RemoveGitWorktreeCommand(IGitService git)
    : ICommandHandler<RemoveGitWorktreeRequest, RemoveGitWorktreeResponse>
{
    public async Task<RemoveGitWorktreeResponse> ExecuteAsync(RemoveGitWorktreeRequest request, CancellationToken cancellationToken = default)
    {
        var mainPath = request.MainRepositoryPath ?? throw new ArgumentException("mainRepositoryPath required");
        var worktreePath = request.WorktreePath ?? throw new ArgumentException("worktreePath required");

        var (success, alreadyRemoved, errorCode, errorMessage) = await git.RemoveWorktreeAsync(
            mainPath,
            worktreePath,
            force: request.Force,
            cancellationToken);

        return new RemoveGitWorktreeResponse
        {
            Success = success,
            AlreadyRemoved = alreadyRemoved,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };
    }
}
