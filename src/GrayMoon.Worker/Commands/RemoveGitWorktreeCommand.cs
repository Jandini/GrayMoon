using GrayMoon.Worker.Services;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

public sealed class RemoveGitWorktreeCommand(
    IGitWorktreeService worktreeService,
    IFileLockInspector lockInspector,
    ILogger<RemoveGitWorktreeCommand> logger)
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

        var response = new RemoveGitWorktreeResponse
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

        var failureKind = success
            ? WorktreeRemovalFailureKind.None
            : WorktreeRemovalFailureClassifier.Classify(errorCode, errorMessage);
        if (!success)
            response.FailureKind = failureKind.ToString();

        // Lock inspection only on the failure path: a folder in use, access denied, or files left behind after Git already
        // unregistered the worktree (on Windows that is how a file or folder held open elsewhere usually shows up).
        if (WorktreeRemovalFailureClassifier.WarrantsLockInspection(failureKind) || (success && residue.ResidueRemaining))
            await AddBlockersAsync(response, worktreePath, cancellationToken);

        return response;
    }

    /// <summary>Never lets a failed inspection replace or hide the removal outcome already in <paramref name="response"/>.</summary>
    private async Task AddBlockersAsync(RemoveGitWorktreeResponse response, string worktreePath, CancellationToken cancellationToken)
    {
        try
        {
            var inspection = await lockInspector.InspectAsync(worktreePath, cancellationToken);
            response.BlockingProcesses = BlockingProcessResponse.From(inspection.Processes);
            response.BlockersMayBeIncomplete = inspection.MayBeIncomplete;
            response.BlockersDiagnostic = inspection.Diagnostic;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not look up the processes using {WorktreePath}", worktreePath);
            response.BlockersMayBeIncomplete = true;
            response.BlockersDiagnostic = "Could not find out which programs are using the folder.";
        }
    }
}
