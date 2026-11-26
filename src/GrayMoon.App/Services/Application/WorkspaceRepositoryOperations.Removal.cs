using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Application;

/// <summary>
/// The paths that end GrayMoon's management of a folder: Remove Workspace, a Source repository leaving a Workspace
/// and disabling the Workspace repository. Each one detaches the folder first (the Worker releases its file watchers
/// and removes GrayMoon's Git hooks, <see cref="WorkspaceRepositoryDetacher"/>) and deletes the link rows only when
/// that succeeded, under the Workspace's structural lock so nothing installs hooks again in between.
/// </summary>
public sealed partial class WorkspaceRepositoryOperations
{
    internal const string StructuralBusyMessage =
        "Another operation is running in this workspace. Wait for it to finish and try again.";

    /// <summary>Why Remove Workspace is refused while Features exist. Shown by the dialog and returned by the operation.</summary>
    public static string FeaturesExistOnRemoveMessage(IReadOnlyList<string> featureNames)
    {
        var names = string.Join(", ", featureNames);
        return featureNames.Count == 1
            ? $"This workspace has 1 Feature: {names}. Remove the Feature first, then remove the workspace."
            : $"This workspace has {featureNames.Count} Features: {names}. Remove the Features first, then remove the workspace.";
    }

    public async Task<IReadOnlyList<string>> GetFeatureNamesAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkspaceFeatures.AsNoTracking()
            .Where(f => f.WorkspaceId == workspaceId)
            .OrderBy(f => f.Name)
            .Select(f => f.Name)
            .ToListAsync(cancellationToken);
    }

    public Task<OperationResult> RemoveWorkspaceAsync(
        int workspaceId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Before the lock: without the Worker nothing can be cleaned, so nothing is tried.
        if (workerBridge.GetUnavailableReason() is { } unavailable)
        {
            return Task.FromResult(OperationResult.Fail(
                $"{DetachFailureText.WorkspaceNotRemoved} {DetachFailureText.WorkerNeeded(unavailable)}"));
        }

        return RunStructuralAsync(
            workspaceId,
            "remove-workspace",
            "Removing workspace...",
            progress,
            DetachFailureText.WorkspaceNotRemoved,
            async (report, token) =>
            {
                string? workspaceName;
                await using (var db = await dbContextFactory.CreateDbContextAsync(token))
                {
                    workspaceName = await db.Workspaces.AsNoTracking()
                        .Where(w => w.WorkspaceId == workspaceId)
                        .Select(w => w.Name)
                        .FirstOrDefaultAsync(token);
                }

                // Removed meanwhile (another tab): the user's intent is already met.
                if (workspaceName is null)
                    return OperationResult.Ok();

                // A Feature whose removal is still running still has its row, so it counts.
                var features = await GetFeatureNamesAsync(workspaceId, token);
                if (features.Count > 0)
                    return OperationResult.Fail(FeaturesExistOnRemoveMessage(features));

                // Read while the rows exist: what the App remembers in memory about these contexts goes with them.
                List<int> contextIds;
                await using (var db = await dbContextFactory.CreateDbContextAsync(token))
                {
                    contextIds = await db.WorkspaceFeatureContexts.AsNoTracking()
                        .Where(c => c.WorkspaceId == workspaceId)
                        .Select(c => c.WorkspaceFeatureContextId)
                        .ToListAsync(token);
                }

                report.Report("Removing Git hooks...");
                var summary = await detacher.DetachAsync(
                    workspaceId,
                    workspaceRepositoryIds: null,
                    workspaceRemoved: true,
                    whileDetached: () => DeleteWorkspaceRowsAsync(workspaceId),
                    token);
                if (!summary.Success)
                    return DetachFailureText.ToFailure(summary, DetachFailureText.WorkspaceNotRemoved, DetachFailureText.RetryRemoveWorkspace);

                foreach (var contextId in contextIds)
                {
                    var id = new GrayMoon.Application.Features.WorkspaceFeatureContextId(contextId);
                    stateFreshness?.ForgetContext(id);
                    stateDriftMonitor?.ForgetContext(id);
                    recomputeCoalescer?.Forget($"{workspaceId}:{contextId}");
                }

                stateFreshness?.ForgetWorkspace(workspaceId);

                logger.LogInformation(
                    "Persistence: removed Workspace. WorkspaceId={WorkspaceId} Name={Name} Repositories={Repositories} ReleasedWatchers={ReleasedWatchers}",
                    workspaceId, workspaceName, summary.Repositories.Count, summary.ReleasedWatchers.Count);
                return WithNotes(OperationResult.Ok(), summary);
            },
            cancellationToken);
    }

    public async Task<OperationResult> SaveWorkspaceAsync(
        int workspaceId,
        string name,
        IReadOnlyCollection<int> repositoryIds,
        string? rootPath,
        WorkspaceType? type = null,
        WorkspaceVersioningMode? versioningMode = null,
        WorkspaceCiProvider? ciProvider = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<int> leaving;
        try
        {
            leaving = await workspaceRepository.GetSourceLinkIdsToRemoveAsync(workspaceId, name, repositoryIds, rootPath, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return OperationResult.Fail(ex.Message);
        }

        // A save that removes no repository leaves every folder managed: no Worker, no lock, as before.
        if (leaving.Count == 0)
            return await UpdateAsync();

        if (workerBridge.GetUnavailableReason() is { } unavailable)
        {
            return OperationResult.Fail(
                $"{DetachFailureText.RepositoriesNotRemoved} {DetachFailureText.WorkerNeeded(unavailable)}");
        }

        return await RunStructuralAsync(
            workspaceId,
            "remove-workspace-repositories",
            "Removing repositories...",
            progress: null,
            DetachFailureText.RepositoriesNotRemoved,
            async (report, token) =>
            {
                // Planned again under the lock: the membership may have changed while it was waited for.
                IReadOnlyList<int> current;
                try
                {
                    current = await workspaceRepository.GetSourceLinkIdsToRemoveAsync(workspaceId, name, repositoryIds, rootPath, token);
                }
                catch (InvalidOperationException ex)
                {
                    return OperationResult.Fail(ex.Message);
                }

                if (current.Count == 0)
                    return await UpdateAsync();

                report.Report("Removing Git hooks...");
                OperationResult? saved = null;
                var summary = await detacher.DetachAsync(
                    workspaceId,
                    current,
                    workspaceRemoved: false,
                    whileDetached: async () => saved = await UpdateAsync(),
                    token);
                if (!summary.Success)
                    return DetachFailureText.ToFailure(summary, DetachFailureText.RepositoriesNotRemoved, DetachFailureText.RetrySave);
                return saved is { Success: false } ? saved : WithNotes(saved ?? OperationResult.Ok(), summary);
            },
            cancellationToken);

        async Task<OperationResult> UpdateAsync()
        {
            try
            {
                await workspaceRepository.UpdateAsync(workspaceId, name, repositoryIds, rootPath, type, versioningMode, ciProvider);
                return OperationResult.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return OperationResult.Fail(ex.Message);
            }
        }
    }

    public async Task<OperationResult> DisableWorkspaceRepositoryAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        // The two refusals that need neither the Worker nor the lock keep their own messages.
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await db.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId, cancellationToken))
                return OperationResult.Fail(FeaturesExistOnDisableMessage);
            if (!await db.WorkspaceRepositories.AnyAsync(
                    l => l.WorkspaceId == workspaceId && l.Role == WorkspaceRepositoryRole.Workspace, cancellationToken))
                return OperationResult.Fail("This Workspace has no Workspace repository.");
        }

        if (workerBridge.GetUnavailableReason() is { } unavailable)
        {
            return OperationResult.Fail(
                $"{DetachFailureText.WorkspaceRepositoryNotDisabled} {DetachFailureText.WorkerNeeded(unavailable)}");
        }

        return await RunStructuralAsync(
            workspaceId,
            "disable-workspace-repository",
            "Disabling Workspace repository...",
            progress: null,
            DetachFailureText.WorkspaceRepositoryNotDisabled,
            async (report, token) =>
            {
                int? linkId;
                await using (var db = await dbContextFactory.CreateDbContextAsync(token))
                {
                    linkId = await db.WorkspaceRepositories.AsNoTracking()
                        .Where(l => l.WorkspaceId == workspaceId && l.Role == WorkspaceRepositoryRole.Workspace)
                        .Select(l => (int?)l.WorkspaceRepositoryId)
                        .FirstOrDefaultAsync(token);
                }

                if (linkId is null)
                    return OperationResult.Fail("This Workspace has no Workspace repository.");

                report.Report("Removing Git hooks...");
                OperationResult? rows = null;
                var summary = await detacher.DetachAsync(
                    workspaceId,
                    [linkId.Value],
                    workspaceRemoved: false,
                    whileDetached: async () => rows = await DisableLinkRowsAsync(workspaceId, token),
                    token);
                if (!summary.Success)
                    return DetachFailureText.ToFailure(summary, DetachFailureText.WorkspaceRepositoryNotDisabled, DetachFailureText.RetrySave);
                return rows is { Success: false } ? rows : WithNotes(rows ?? OperationResult.Ok(), summary);
            },
            cancellationToken);
    }

    /// <summary>
    /// A Source repository became the Workspace repository: its old folder stays on disk and is no longer managed, so
    /// its hooks and watcher go before its link state does. The move itself is already done, so a folder that cannot
    /// be cleaned is reported as a warning, with the file, and does not undo the move.
    /// </summary>
    private async Task<string?> DetachAndReplaceMovedSourceAsync(int workspaceRepositoryId, int repositoryId, string repositoryName, int workspaceId)
    {
        string? warning = null;
        try
        {
            var summary = await detacher.DetachSourceFolderAsync(workspaceId, repositoryId, repositoryName, CancellationToken.None);
            if (!summary.Success)
                warning = $"The old folder '{repositoryName}' still has GrayMoon's Git hooks. {DetachResidue(summary)}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not clean the old Source folder. WorkspaceId={WorkspaceId} RepositoryId={RepositoryId}", workspaceId, repositoryId);
            warning = $"The old folder '{repositoryName}' still has GrayMoon's Git hooks: {ex.Message}";
        }

        await ReplaceMovedSourceLinkAsync(workspaceRepositoryId, repositoryId, workspaceId);
        return warning;
    }

    /// <summary>Detaches links on a rollback path that cannot fail the caller; a problem is logged with the folder.</summary>
    private async Task DetachQuietlyAsync(int workspaceId, IReadOnlyCollection<int> workspaceRepositoryIds)
    {
        try
        {
            var summary = await detacher.DetachAsync(workspaceId, workspaceRepositoryIds, workspaceRemoved: false, cancellationToken: CancellationToken.None);
            if (!summary.Success)
            {
                logger.LogWarning(
                    "A link that is being rolled back could not be cleaned on disk. WorkspaceId={WorkspaceId} Detail={Detail}",
                    workspaceId, DetachResidue(summary));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A link that is being rolled back could not be cleaned on disk. WorkspaceId={WorkspaceId}", workspaceId);
        }
    }

    /// <summary>One sentence per problem, for a rollback or warning text that has no list to show.</summary>
    private static string DetachResidue(DetachSummary summary) =>
        summary.Unavailable is { } unavailable
            ? $"The Git hooks GrayMoon installed could not be removed: {unavailable}"
            : string.Join(" ", summary.Failed.Select(DetachFailureText.Describe));

    private static OperationResult WithNotes(OperationResult result, DetachSummary summary)
    {
        var notes = DetachFailureText.SuccessNotes(summary);
        if (!result.Success || notes.Count == 0)
            return result;

        var all = (result.Warnings ?? []).Concat(notes).ToList();
        return result with { Warnings = all, WarningSummary = string.Join(" ", all) };
    }

    /// <summary>
    /// Deletes the Workspace row; its links, contexts and state rows go with it through the database's cascades. Runs
    /// only after every folder was detached, on its own context (the caller holds none).
    /// </summary>
    private async Task DeleteWorkspaceRowsAsync(int workspaceId)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId);
        if (workspace is null)
            return;

        db.Workspaces.Remove(workspace);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Runs <paramref name="work"/> under the Workspace's structural lock and returns its result. A lock that is held
    /// fails with <see cref="StructuralBusyMessage"/> and changes nothing.
    /// </summary>
    private async Task<OperationResult> RunStructuralAsync(
        int workspaceId,
        string operationKind,
        string displayMessage,
        IProgress<OperationProgress>? progress,
        string notDone,
        Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult>> work,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = operationLock.TryStartStructural(
            workspaceId,
            operationKind,
            WorkspaceJobKeys.RepositoriesOverlayKey(workspaceId),
            displayMessage,
            async (op, ct) =>
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                    tcs.TrySetResult(await work(BindProgress(op, progress), linked.Token));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "{OperationKind} threw. WorkspaceId={WorkspaceId}", operationKind, workspaceId);
                    tcs.TrySetResult(OperationResult.Fail($"{notDone} {ex.Message}"));
                }
            },
            out var operation);

        if (!started)
            return OperationResult.Fail($"{notDone} {StructuralBusyMessage}");

        var result = await tcs.Task.WaitAsync(cancellationToken);
        await operation.WhenCompleted;
        return result;
    }
}
