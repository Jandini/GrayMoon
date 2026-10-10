using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Sequences the Prepare Workspace workflow: parallel branch creation with inline state sync (hooks suppressed), then
/// - only when every targeted repository is on the new branch - the dependency update, the push, or both as the
/// two-lane Update and Push. Any branch failure, unknown outcome or cancellation stops the workflow before anything
/// is updated, committed or pushed; branches already created are left as they are.
/// </summary>
public sealed class PrepareWorkspaceOrchestrator(
    IPrepareWorkspacePhases phases,
    ILogger<PrepareWorkspaceOrchestrator> logger)
{
    public const string NoRepositoriesMessage = "Prepare Workspace stopped: no repositories were selected.";

    public async Task<PrepareWorkspaceResult> RunAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string newBranchName,
        string baseBranch,
        IReadOnlySet<int>? repositoryIds,
        bool updateDependencies,
        bool pushChanges,
        string? commitMessage,
        IProgress<OperationProgress>? progress,
        Action<int, string> setRepositoryError,
        Action<int, string> setLevelError,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "PrepareWorkspaceOrchestrator starting for workspace {WorkspaceId}: branch={Branch}, updateDeps={UpdateDeps}, push={Push}",
            workspaceId, newBranchName, updateDependencies, pushChanges);

        var sink = new OperationErrorSink(workspaceId, logger, setRepositoryError, setLevelError);

        // An empty selection means everything was deliberately left out; branch creation would read it as "all repos".
        if (repositoryIds is { Count: 0 })
        {
            sink.Level(0, NoRepositoriesMessage);
            return PrepareWorkspaceResult.BranchesFailed(new Dictionary<int, string>());
        }

        progress.Report("Creating branches...");
        var outcome = await phases.CreateBranchesAsync(
            workspaceId, contextId, newBranchName, baseBranch, repositoryIds, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Branch gate: every later phase mutates repositories, so all of them must be on the new branch first.
        var branchErrors = GetBranchFailures(repositoryIds, outcome, newBranchName);
        if (branchErrors.Count > 0 || outcome.TargetedRepositoryIds.Count == 0)
        {
            foreach (var (repoId, msg) in branchErrors)
                sink.Repository(repoId, msg);
            sink.Level(0, outcome.TargetedRepositoryIds.Count == 0 ? NoRepositoriesMessage : PrepareWorkspaceResult.BranchesStoppedMessage);
            logger.LogWarning(
                "PrepareWorkspaceOrchestrator stopped for workspace {WorkspaceId}: branch creation failed in {FailedCount} of {TargetedCount} repositories",
                workspaceId, branchErrors.Count, outcome.TargetedRepositoryIds.Count);
            return PrepareWorkspaceResult.BranchesFailed(branchErrors);
        }

        var noBranchErrors = new Dictionary<int, string>();

        if (updateDependencies && pushChanges)
        {
            var pipeline = await phases.UpdateAndPushAsync(
                workspaceId,
                contextId,
                commitMessage,
                message => progress.Report(message),
                setRepositoryError,
                setLevelError,
                cancellationToken);
            if (pipeline.Pipelined)
            {
                logger.LogInformation("PrepareWorkspaceOrchestrator completed for workspace {WorkspaceId} (two-lane). Success={Success}", workspaceId, pipeline.Success);
                return new PrepareWorkspaceResult(true, noBranchErrors, Pipeline: pipeline);
            }

            // Nothing ran: the update runs here and the caller pushes with its own checks and confirmation.
            logger.LogInformation("PrepareWorkspaceOrchestrator workspace {WorkspaceId}: two-lane push not possible, updating then handing the push back.", workspaceId);
        }

        DependencyUpdateRunResult? update = null;
        if (updateDependencies)
        {
            progress.Report("Updating dependencies...");
            update = await phases.UpdateDependenciesAsync(
                workspaceId, contextId, commitMessage, progress, setRepositoryError, setLevelError, cancellationToken);
        }

        logger.LogInformation("PrepareWorkspaceOrchestrator completed for workspace {WorkspaceId}", workspaceId);
        return new PrepareWorkspaceResult(
            true,
            noBranchErrors,
            Update: update,
            PushPending: pushChanges && (update?.Success ?? true));
    }

    /// <summary>
    /// Every repository that is not provably on <paramref name="newBranchName"/>: reported failures, requested
    /// repositories that were never attempted, attempts with no reported outcome, and successes whose checked-out
    /// branch is unknown or different.
    /// </summary>
    internal static Dictionary<int, string> GetBranchFailures(
        IReadOnlySet<int>? requestedRepositoryIds,
        BranchCreationOutcome outcome,
        string newBranchName)
    {
        var failures = new Dictionary<int, string>(outcome.ErrorsByRepositoryId);

        if (requestedRepositoryIds != null)
        {
            foreach (var repoId in requestedRepositoryIds.Where(id => !outcome.TargetedRepositoryIds.Contains(id)))
                failures.TryAdd(repoId, "Branch was not created: the repository was not found in this workspace.");
        }

        foreach (var repoId in outcome.TargetedRepositoryIds)
        {
            if (failures.ContainsKey(repoId))
                continue;

            if (!outcome.CheckedOutBranchByRepositoryId.TryGetValue(repoId, out var checkedOut))
                failures[repoId] = "Branch creation did not report a result for this repository.";
            else if (string.IsNullOrWhiteSpace(checkedOut))
                failures[repoId] = $"Could not confirm that '{newBranchName}' is checked out.";
            else if (!string.Equals(checkedOut, newBranchName, StringComparison.Ordinal))
                failures[repoId] = $"Expected '{newBranchName}' to be checked out, found '{checkedOut}'.";
        }

        return failures;
    }
}
