using GrayMoon.Worker.Services;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

public sealed class CreateGitWorktreeCommand(IGitService git, IGitWorktreeService worktreeService)
    : ICommandHandler<CreateGitWorktreeRequest, CreateGitWorktreeResponse>
{
    public async Task<CreateGitWorktreeResponse> ExecuteAsync(CreateGitWorktreeRequest request, CancellationToken cancellationToken = default)
    {
        var mainPath = request.MainRepositoryPath ?? throw new ArgumentException("mainRepositoryPath required");
        var worktreePath = request.WorktreePath ?? throw new ArgumentException("worktreePath required");
        var branchName = request.Detach
            ? null
            : request.BranchName ?? throw new ArgumentException("branchName required");
        var baseCommitSha = request.BaseCommitSha ?? throw new ArgumentException("baseCommitSha required");

        // git worktree add fires post-checkout from the new worktree using the common hooks directory.
        // Hooks written by older workers embed the main checkout path, which would attribute this
        // worktree's checkout to the special Workspace.
        if (request.WorkspaceId is > 0 && request.RepositoryId is > 0 && Directory.Exists(mainPath))
            await git.WriteSyncHooksAsync(mainPath, request.WorkspaceId.Value, request.RepositoryId.Value, cancellationToken);

        var (success, worktree, alreadyExisted, errorCode, errorMessage) = await worktreeService.CreateWorktreeAsync(
            mainPath,
            worktreePath,
            branchName,
            baseCommitSha,
            cancellationToken);

        if (success && branchName != null)
        {
            var pathForMeta = worktree?.WorktreePath ?? worktreePath;
            await git.SetDivergenceBaseBranchAsync(pathForMeta, request.DivergenceBaseBranch, cancellationToken);
        }

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
