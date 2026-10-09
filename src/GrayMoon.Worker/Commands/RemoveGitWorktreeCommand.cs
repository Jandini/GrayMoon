using GrayMoon.Worker.Services;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

public sealed class RemoveGitWorktreeCommand(IGitWorktreeService worktreeService)
    : ICommandHandler<RemoveGitWorktreeRequest, RemoveGitWorktreeResponse>
{
    public async Task<RemoveGitWorktreeResponse> ExecuteAsync(RemoveGitWorktreeRequest request, CancellationToken cancellationToken = default)
    {
        var mainPath = request.MainRepositoryPath ?? throw new ArgumentException("mainRepositoryPath required");
        var worktreePath = request.WorktreePath ?? throw new ArgumentException("worktreePath required");

        var (success, alreadyRemoved, errorCode, errorMessage, residue) = await worktreeService.RemoveWorktreeAsync(
            mainPath,
            worktreePath,
            force: request.Force,
            cancellationToken,
            featureRootPath: request.FeatureRootPath,
            featureStorageRoot: request.FeatureStorageRoot,
            unlock: request.Unlock);

        return new RemoveGitWorktreeResponse
        {
            Success = success,
            AlreadyRemoved = alreadyRemoved,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ResidueRemaining = residue.ResidueRemaining,
            ResidueFileCount = residue.ResidueFileCount,
            ResidueSampleFiles = residue.ResidueSampleFiles.Count > 0 ? [.. residue.ResidueSampleFiles] : null,
            ResidueMessage = residue.ResidueMessage,
            FailureKind = success ? null : WorktreeRemovalFailureClassifier.Classify(errorCode, errorMessage).ToString(),
        };
    }
}
