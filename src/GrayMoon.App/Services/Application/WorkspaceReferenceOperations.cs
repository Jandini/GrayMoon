using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Policy;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.WorkspaceManifest;
using GrayMoon.Application.Workspaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Application;

public sealed class WorkspaceReferenceOperations(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkspaceOperationLock operationLock,
    IWorkerBridge workerBridge,
    ReferenceActivationChecker activationChecker,
    ReferenceRevisionRecorder revisionRecorder,
    IWorkspaceManifestService manifestService,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<WorkspaceReferenceOperations> logger) : IWorkspaceReferenceOperations
{
    internal const string BusyMessage = "An operation is running for this Workspace.";

    internal const string BlockedMessage = "Nothing was changed. Some repositories have work that is only on this machine.";

    internal const string WorkerNotConnectedMessage =
        "Worker not connected. Start the GrayMoon Worker so the repositories can be checked before they become read only.";

    internal const string NoWorkspaceRepositoryNotice =
        "This Workspace has no Workspace repository, so the read only state and the commits of read only repositories are not recorded in a Workspace definition. Restoring it on another computer needs a Workspace repository.";

    public async Task<WorkspaceReferenceState> GetStateAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var referenceDefault = await db.Workspaces
            .AsNoTracking()
            .Where(workspace => workspace.WorkspaceId == workspaceId)
            .Select(workspace => workspace.ReferenceDefault)
            .FirstOrDefaultAsync(cancellationToken);
        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(link => link.WorkspaceId == workspaceId)
            .Select(link => new { link.RepositoryId, link.Repository!.RepositoryName, link.IsReference, link.Role, link.Repository!.Archived })
            .ToListAsync(cancellationToken);

        var sources = links
            .Where(link => link.Role == WorkspaceRepositoryRole.Source)
            .OrderBy(link => link.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .Select(link => new WorkspaceReferenceRepositoryState(link.RepositoryId, link.RepositoryName, link.IsReference) { Archived = link.Archived })
            .ToList();
        var referenceCount = sources.Count(source => source.IsReference);
        var aggregate = WorkspaceReferenceAggregates.Of(sources.Count, referenceCount);

        return new WorkspaceReferenceState(
            referenceDefault,
            aggregate,
            sources,
            links.Any(link => link.Role == WorkspaceRepositoryRole.Workspace));
    }

    public Task<WorkspaceReferenceChangeResult> SetAllAsync(
        int workspaceId,
        bool reference,
        bool alsoSetDefault,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunLockedAsync(
            workspaceId,
            reference ? "Making repositories read only..." : "Making repositories writable...",
            progress,
            (report, ct) => ChangeAsync(workspaceId, repositoryId: null, reference, alsoSetDefault ? reference : null, report, ct),
            cancellationToken);

    public Task<WorkspaceReferenceChangeResult> SetDefaultAsync(int workspaceId, bool referenceDefault, CancellationToken cancellationToken = default) =>
        RunLockedAsync(
            workspaceId,
            "Saving the read only default...",
            progress: null,
            (report, ct) => ChangeAsync(workspaceId, repositoryId: null, reference: null, referenceDefault, report, ct),
            cancellationToken,
            WorkspaceJobKeys.ReferenceDefaultOverlayKey(workspaceId));

    public Task<WorkspaceReferenceChangeResult> SetRepositoryAsync(
        int workspaceId,
        int repositoryId,
        bool reference,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunLockedAsync(
            workspaceId,
            reference ? "Making the repository read only..." : "Making the repository writable...",
            progress,
            (report, ct) => ChangeAsync(workspaceId, repositoryId, reference, referenceDefault: null, report, ct),
            cancellationToken);

    /// <summary>
    /// The structural lock: the change cannot start while any operation runs for the Workspace, and no operation
    /// can start while it runs. So an operation that has already chosen its repositories keeps a true set.
    /// </summary>
    private async Task<WorkspaceReferenceChangeResult> RunLockedAsync(
        int workspaceId,
        string displayMessage,
        IProgress<OperationProgress>? progress,
        Func<IProgress<OperationProgress>, CancellationToken, Task<WorkspaceReferenceChangeResult>> work,
        CancellationToken cancellationToken,
        string? overlayKey = null)
    {
        var tcs = new TaskCompletionSource<WorkspaceReferenceChangeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = operationLock.TryStartStructural(
            workspaceId,
            "reference-mode",
            overlayKey ?? WorkspaceJobKeys.RepositoriesOverlayKey(workspaceId),
            displayMessage,
            async (op, ct) =>
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                    var report = new Progress(op, progress);
                    tcs.TrySetResult(await work(report, linked.Token));
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult(WorkspaceReferenceChangeResult.Failed("The change was cancelled."));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Changing read only mode threw. WorkspaceId={WorkspaceId}", workspaceId);
                    tcs.TrySetResult(WorkspaceReferenceChangeResult.Failed(ex.Message));
                }
            },
            out var operation);

        if (!started)
            return WorkspaceReferenceChangeResult.Failed(BusyMessage);

        var result = await tcs.Task;
        await operation.WhenCompleted;
        return result;
    }

    /// <param name="repositoryId">One Source repository, or null for all of them.</param>
    /// <param name="reference">The new state of the selected repositories, or null to leave them alone.</param>
    /// <param name="referenceDefault">The new default, or null to leave it alone.</param>
    private async Task<WorkspaceReferenceChangeResult> ChangeAsync(
        int workspaceId,
        int? repositoryId,
        bool? reference,
        bool? referenceDefault,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, cancellationToken);
        if (workspace is null)
            return WorkspaceReferenceChangeResult.Failed("Workspace not found.");

        var links = await db.WorkspaceRepositories
            .Where(link => link.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        var hasWorkspaceRepository = links.Any(link => link.Role == WorkspaceRepositoryRole.Workspace);
        // The Workspace repository is never read-only, so it is never selected.
        var selected = links
            .Where(link => link.Role == WorkspaceRepositoryRole.Source && (repositoryId is null || link.RepositoryId == repositoryId))
            .ToList();
        if (repositoryId is not null && selected.Count == 0)
            return WorkspaceReferenceChangeResult.Failed("The repository is not a Source repository of this Workspace.");

        var changing = reference is null ? [] : selected.Where(link => link.IsReference != reference.Value).ToList();
        var defaultChanges = referenceDefault is not null && workspace.ReferenceDefault != referenceDefault.Value;
        if (changing.Count == 0 && !defaultChanges)
            return new WorkspaceReferenceChangeResult { Success = true };

        var warnings = new List<string>();
        ReferenceActivationReport? report = null;
        if (reference == true && changing.Count > 0)
        {
            if (!workerBridge.IsWorkerConnected)
                return WorkspaceReferenceChangeResult.Failed(WorkerNotConnectedMessage);

            progress.Report(new OperationProgress("Checking repositories..."));
            report = await activationChecker.CheckAsync(workspaceId, [.. changing.Select(link => link.RepositoryId)], cancellationToken, progress);
            if (report.IsBlocked)
            {
                return new WorkspaceReferenceChangeResult
                {
                    Success = false,
                    Error = BlockedMessage,
                    Blocks = [.. report.Blocks.Select(ToFinding)],
                    Warnings = [.. report.Warnings.Select(finding => finding.Message)],
                };
            }

            warnings.AddRange(report.Warnings.Select(finding => finding.Message));
        }

        // Flags and default in one save: a reader never sees half of a change.
        foreach (var link in changing)
            link.IsReference = reference!.Value;
        if (defaultChanges)
            workspace.ReferenceDefault = referenceDefault!.Value;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Persistence: saved read only mode. WorkspaceId={WorkspaceId}, Reference={Reference}, RepositoryIds=[{RepositoryIds}], Default={Default}",
            workspaceId, reference, string.Join(", ", changing.Select(link => link.RepositoryId)), defaultChanges ? referenceDefault : null);

        if (hasWorkspaceRepository)
        {
            // The definition is rewritten from the database: flags for every entry, and the recorded branch and
            // commit of a repository that is writable again are dropped by that write.
            progress.Report(new OperationProgress("Writing Workspace definition..."));
            var definitionError = await manifestService.SyncDefinitionToDiskAsync(workspaceId, cancellationToken);
            if (definitionError is not null)
            {
                warnings.Add(definitionError);
            }
            else if (report is not null)
            {
                var revisions = report.Heads
                    .Select(head => new ReferenceRevision(head.Key, head.Value.Branch, head.Value.Commit))
                    .ToList();
                var recordWarning = await revisionRecorder.RecordAsync(workspaceId, revisions, cancellationToken);
                if (recordWarning is not null)
                    warnings.Add(recordWarning);
            }
        }

        // Open pages reload their rows, badges and notifications.
        await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);

        var anyReference = links.Any(link => link.Role == WorkspaceRepositoryRole.Source && link.IsReference) || workspace.ReferenceDefault;
        return new WorkspaceReferenceChangeResult
        {
            Success = true,
            Warnings = warnings,
            Notice = !hasWorkspaceRepository && anyReference ? NoWorkspaceRepositoryNotice : null,
            ChangedRepositoryCount = changing.Count,
        };
    }

    private static WorkspaceReferenceFinding ToFinding(ReferenceActivationFinding finding) =>
        new(finding.RepositoryId, finding.RepositoryName, finding.FeatureName, finding.Message);

    private sealed class Progress(IWorkspaceLockedOperation operation, IProgress<OperationProgress>? inner) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value)
        {
            operation.ReportProgress(value.Message);
            inner?.Report(value);
        }
    }
}
