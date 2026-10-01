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

        var (success, alreadyRemoved, errorCode, errorMessage, residue) = await git.RemoveWorktreeAsync(
            mainPath,
            worktreePath,
            force: request.Force,
            cancellationToken,
            featureRootPath: request.FeatureRootPath,
            featureStorageRoot: request.FeatureStorageRoot);

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
        };
    }
}
