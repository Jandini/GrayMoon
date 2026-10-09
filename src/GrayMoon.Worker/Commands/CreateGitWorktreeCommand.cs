using GrayMoon.Worker.Services;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

public sealed class CreateGitWorktreeCommand(IGitService git, IGitWorktreeService worktreeService, ILogger<CreateGitWorktreeCommand>? logger = null)
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
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (request.WorkspaceId is > 0 && request.RepositoryId is > 0 && Directory.Exists(mainPath))
            await git.WriteSyncHooksAsync(mainPath, request.WorkspaceId.Value, request.RepositoryId.Value, cancellationToken);
        var hooksMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var worktreeStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        var (success, worktree, alreadyExisted, errorCode, errorMessage) = await worktreeService.CreateWorktreeAsync(
            mainPath,
            worktreePath,
            branchName,
            baseCommitSha,
            cancellationToken);

        var worktreeMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(worktreeStartedAt).TotalMilliseconds;
        var divergenceStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        if (success && branchName != null)
        {
            var pathForMeta = worktree?.WorktreePath ?? worktreePath;
            await git.SetDivergenceBaseBranchAsync(pathForMeta, request.DivergenceBaseBranch, cancellationToken);
        }

        logger?.LogInformation(
            "CreateGitWorktree timing. Repo={MainRepositoryPath} Success={Success} AlreadyExisted={AlreadyExisted} HooksMs={HooksMs} WorktreeMs={WorktreeMs} DivergenceMs={DivergenceMs} TotalMs={TotalMs}",
            mainPath, success, alreadyExisted, hooksMs, worktreeMs,
            (long)System.Diagnostics.Stopwatch.GetElapsedTime(divergenceStartedAt).TotalMilliseconds,
            (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

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
