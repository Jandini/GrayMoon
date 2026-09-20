using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class CreateGitWorktreeCommand(IGitService git)
    : ICommandHandler<CreateGitWorktreeRequest, CreateGitWorktreeResponse>
{
    public async Task<CreateGitWorktreeResponse> ExecuteAsync(CreateGitWorktreeRequest request, CancellationToken cancellationToken = default)
    {
        var mainPath = request.MainRepositoryPath ?? throw new ArgumentException("mainRepositoryPath required");
        var worktreePath = request.WorktreePath ?? throw new ArgumentException("worktreePath required");
        var branchName = request.BranchName ?? throw new ArgumentException("branchName required");
        var baseCommitSha = request.BaseCommitSha ?? throw new ArgumentException("baseCommitSha required");

        var (success, worktree, alreadyExisted, errorCode, errorMessage) = await git.CreateWorktreeAsync(
            mainPath,
            worktreePath,
            branchName,
            baseCommitSha,
            cancellationToken);

        return new CreateGitWorktreeResponse
        {
            Success = success,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            AlreadyExisted = alreadyExisted,
            Worktree = worktree,
            WorktreePath = worktree?.WorktreePath,
            HeadSha = worktree?.HeadSha,
            BranchName = worktree?.BranchName,
        };
    }
}
