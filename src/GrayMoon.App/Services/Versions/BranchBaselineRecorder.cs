using GrayMoon.App.Models.Api;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Versions;

/// <summary>
/// Records the branch point of a branch GrayMoon just created in the special Workspace (design 5.2), from the Worker's
/// <c>CreateBranch</c> answer. Nothing is recorded for a branch that already existed, a repository without commits,
/// or a Feature (its <see cref="Models.WorkspaceFeatureRepository"/> row is the baseline). A failed write leaves the
/// branch in place and returns the warning the operation reports.
/// </summary>
public static class BranchBaselineRecorder
{
    public static async Task<string?> RecordAsync(
        WorkspaceRepositoryStateWriter stateWriter,
        WorkspaceFeatureContextId contextId,
        bool isSpecialWorkspace,
        int workspaceRepositoryId,
        string repositoryName,
        string branchName,
        string baseBranchName,
        CreateBranchResponse? response,
        DateTime nowUtc,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!isSpecialWorkspace || response is not { Success: true, AlreadyExisted: false } || string.IsNullOrWhiteSpace(response.BaseCommitSha))
            return null;

        try
        {
            await stateWriter.RecordBranchBaselineAsync(
                contextId, workspaceRepositoryId, branchName, baseBranchName, response.BaseCommitSha, response.ResolvedStartRef, nowUtc, cancellationToken);
            logger.LogInformation(
                "Branch baseline recorded: repository {Repository}, branch {Branch}, parent {Parent}, start {StartRef}, commit {Commit}",
                repositoryName, branchName, baseBranchName, response.ResolvedStartRef, response.BaseCommitSha);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Branch baseline of {Repository} ({Branch}) could not be recorded", repositoryName, branchName);
            return $"{repositoryName}: branch {branchName} was created, but where it started could not be recorded ({ex.Message}). It will be pushed like a branch GrayMoon did not create.";
        }
    }
}
