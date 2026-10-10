using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.Git;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using GrayMoon.Common.Features;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.Features;

public sealed class WorkspaceFeatureOperations(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IServiceScopeFactory scopeFactory,
    IWorkspaceOperationLock operationLock,
    IWorkspaceFeatureContextResolver contextResolver,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceSelectedFeatureContextService selectedContextService,
    IWorkerBridge workerBridge,
    WorkspaceService workspaceService,
    WorkspacePullRequestService workspacePullRequestService,
    IWorkspaceGitChangesMonitoringPause gitChangesMonitoringPause,
    IWorkspaceCapabilitiesResolver capabilitiesResolver,
    IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<WorkspaceFeatureOperations> logger,
    FeatureFinalizationCoordinator? finalizationCoordinator = null,
    FeatureCodeGraphService? codeGraph = null) : IWorkspaceFeatureOperations
{
    private int MaxParallel => Math.Max(1, workspaceOptions.Value.MaxParallelOperations);

    public async Task<CreateFeatureResult> CreateFeatureAsync(
        int workspaceId,
        string featureName,
        WorkspaceFeatureBaseKindApplication baseKind,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var name = (featureName ?? string.Empty).Trim();
        var validationError = FeatureNameValidator.Validate(name);
        if (validationError is not null)
            return FailCreate("InvalidFeatureName", validationError);

        if (baseKind != WorkspaceFeatureBaseKindApplication.CurrentWorkspace)
            return FailCreate("UnsupportedBaseKind", "Only Current Workspace base is supported.");

        var tcs = new TaskCompletionSource<CreateFeatureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlayKey = WorkspaceJobKeys.RepositoriesOverlayKey(workspaceId);
        var started = operationLock.TryStartStructural(
            workspaceId,
            "create-feature",
            overlayKey,
            "Creating feature...",
            async (op, ct) =>
            {
                var startedAt = logger.FeatureOperationStarted("Create", workspaceId, null, null, name);
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                    var overlayProgress = BindOverlayProgress(op, progress);
                    var result = await CreateFeatureCoreAsync(workspaceId, name, overlayProgress, linked.Token);
                    logger.FeatureOperationFinished(
                        "Create", workspaceId, result.WorkspaceFeatureId, result.ContextId?.Value, name, startedAt,
                        result.Success ? "Succeeded" : result.Condition ?? "Failed", result.Error);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Feature Create threw. WorkspaceId={WorkspaceId} FeatureName={FeatureName}", workspaceId, name);
                    logger.FeatureOperationFinished("Create", workspaceId, null, null, name, startedAt, "Exception", ex.Message);
                    tcs.TrySetResult(FailCreate("Exception", ex.Message));
                    throw;
                }
            },
            out var operation);

        if (!started)
        {
            logger.LogInformation(
                "Feature Create refused because the Workspace is busy. WorkspaceId={WorkspaceId} FeatureName={FeatureName}",
                workspaceId, name);
            return FailCreate("WorkspaceBusy", "A Workspace structural operation is already running.");
        }

        var created = await tcs.Task.WaitAsync(cancellationToken);
        await operation.WhenCompleted;
        return created;
    }

    private async Task<CreateFeatureResult> CreateFeatureCoreAsync(
        int workspaceId,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stageStart = Stopwatch.GetTimestamp();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var normalizedName = name.ToLowerInvariant();
        if (await db.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId && f.Name.ToLower() == normalizedName, cancellationToken))
            return FailCreate("DuplicateName", $"A Feature named '{name}' already exists.");

        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace {workspaceId} was not found.");

        await EnsureManagedFeatureStorageRootAsync(workspace, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        // A Feature removed earlier under this name may have left its folder marked pending deletion; finish that first.
        // Only a marked folder is touched, so a folder the user made there by hand still fails below as before.
        var pendingFolder = await CleanupFeatureFolderForNameAsync(workspaceId, name, onlyIfMarked: true, cancellationToken);
        if (pendingFolder is not null)
        {
            return FailCreate(
                "FeatureFolderPendingDeletion",
                $"A previous Feature named '{name}' is still being cleaned up because some of its files are in use. "
                + $"Close the programs using {pendingFolder} or choose another name.");
        }

        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Include(l => l.Repository)
            .Where(l => l.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
            return FailCreate("NoRepositories", "Workspace has no repositories.");

        var specialContextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var repoNames = links
            .Select(l => l.Repository?.RepositoryName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Cast<string>()
            .ToList();

        var workspaceRepositoryName = links
            .FirstOrDefault(l => l.Role == WorkspaceRepositoryRole.Workspace)?.Repository?.RepositoryName;
        stageStart = logger.FeatureStageFinished("Create", workspaceId, name, "PreFlight", stageStart, $"Repositories={repoNames.Count}");
        var snapshot = await GetHeadSnapshotAsync(workspace, repoNames, workspaceRepositoryName, name, cancellationToken);
        stageStart = logger.FeatureStageFinished(
            "Create", workspaceId, name, "HeadSnapshot", stageStart,
            $"Repositories={repoNames.Count} Resolved={snapshot.Commits.Count} Collisions={snapshot.BranchCollisions.Count}");
        if (snapshot.Commits.Count != repoNames.Count)
            return FailCreate("HeadCommitsIncomplete", "Could not resolve HEAD for every Workspace repository.");

        // Validate every repo HEAD before writing any Feature rows (C1).
        foreach (var link in links)
        {
            var repoName = link.Repository!.RepositoryName;
            if (!snapshot.Commits.TryGetValue(repoName, out var sha) || string.IsNullOrWhiteSpace(sha))
                return FailCreate("HeadCommitsIncomplete", $"Missing HEAD for repository '{repoName}'.");
        }

        if (snapshot.BranchCollisions.Count > 0)
        {
            var collisions = snapshot.BranchCollisions
                .OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                .Select(c => new CreateFeatureBranchCollision
                {
                    RepositoryName = c.Key,
                    Refs = c.Value
                })
                .ToList();
            return new CreateFeatureResult
            {
                Success = false,
                Condition = "BranchExists",
                Error = $"Branch '{name}' already exists in {collisions.Count} of {repoNames.Count} repositories.",
                BranchCollisions = collisions,
                TotalRepositoryCount = repoNames.Count
            };
        }

        // Build worktree paths from the already-persisted storage root. Do not call pathResolver
        // inside the intent transaction: it opens a second AppDbContext and deadlocks SQLite
        // against this write (and against WorkspaceGitChangesWriteQueue).
        if (string.IsNullOrWhiteSpace(workspace.ManagedFeatureStorageRoot))
            throw new InvalidOperationException(
                "Feature storage root is not configured. Set it on the Settings page (or connect the Worker so the host user profile can be used as the default).");
        var featureRootPath = WorkerPath.Combine(workspace.ManagedFeatureStorageRoot, name);

        var now = DateTime.UtcNow;
        var feature = new WorkspaceFeature
        {
            WorkspaceId = workspaceId,
            Name = name,
            LifecycleState = WorkspaceFeatureLifecycleState.Creating,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = now,
            UpdatedAt = now
        };

        WorkspaceFeatureContext context;
        WorkspaceFeatureContextId contextId;
        var pendingRows = new List<WorkspaceFeatureRepository>();

        // Feature + context + Pending rows commit together or not at all (C1).
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                db.WorkspaceFeatures.Add(feature);
                await db.SaveChangesAsync(cancellationToken);

                context = new WorkspaceFeatureContext
                {
                    WorkspaceId = workspaceId,
                    Kind = WorkspaceFeatureContextKind.Feature,
                    WorkspaceFeatureId = feature.WorkspaceFeatureId,
                    CreatedAt = now,
                    IsInSync = false
                };
                db.WorkspaceFeatureContexts.Add(context);
                await db.SaveChangesAsync(cancellationToken);

                contextId = new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
                foreach (var link in links)
                {
                    var repoName = link.Repository!.RepositoryName;
                    var sha = snapshot.Commits[repoName];

                    // Parent branch from the same worker snapshot as BaseCommitSha. Detached HEAD -> null
                    // (do not invent a name from mutable Workspace link state).
                    snapshot.Branches.TryGetValue(repoName, out var parentBranch);
                    parentBranch = string.IsNullOrWhiteSpace(parentBranch) ? null : parentBranch.Trim();

                    // Repositories on a tag stay on that tag in the Feature: detached worktree, no Feature branch.
                    snapshot.Tags.TryGetValue(repoName, out var pinnedTag);
                    pinnedTag = string.IsNullOrWhiteSpace(pinnedTag) ? null : pinnedTag.Trim();

                    var row = new WorkspaceFeatureRepository
                    {
                        WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                        WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                        // The Workspace-role repository's worktree is the Feature root itself (D10).
                        WorktreePath = link.Role == WorkspaceRepositoryRole.Workspace
                            ? featureRootPath
                            : WorkerPath.Combine(featureRootPath, repoName),
                        BaseCommitSha = sha,
                        ParentBranchName = pinnedTag == null ? parentBranch : null,
                        PinnedTag = pinnedTag,
                        CreatedAt = now,
                        State = WorkspaceFeatureRepositoryState.Pending
                    };
                    db.WorkspaceFeatureRepositories.Add(row);
                    pendingRows.Add(row);
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        stageStart = logger.FeatureStageFinished("Create", workspaceId, name, "IntentTransaction", stageStart);

        // From here on, the Feature + context + Pending rows already committed above are real DB state -
        // any exception (including the user pressing the overlay's Abort button, which cancels
        // cancellationToken) must not leave LifecycleState stuck at Creating forever with no way for the
        // Feature selector to open or remove it. Treat it exactly like a partial per-repo failure: mark
        // NeedsRepair and hand back a NeedsRepair result so CreateFeatureModal opens Status and repair
        // (Retry re-attempts the still-Pending repos; Roll back/Remove clean up).
        //
        // The Worker releases its deferred checkout syncs shortly after the last worktree exists, before the seed
        // below has run. Armed from here to the end of the method (including the failure write), the barrier keeps
        // those syncs from racing the seed; it is in memory only, so it cannot outlive a crash.
        using var finalizationBarrier = finalizationCoordinator?.Arm(contextId.Value);
        try
        {
            var anyFailure = 0;
            var createCompleted = 0;
            var createTotal = pendingRows.Count;
            var capabilities = (await capabilitiesResolver.GetAsync(workspaceId, cancellationToken)).ToRepositoryOperationCapabilities();
            using var gate = new SemaphoreSlim(MaxParallel);
            var rootRow = pendingRows.SingleOrDefault(r => links.First(l => l.WorkspaceRepositoryId == r.WorkspaceRepositoryId).Role == WorkspaceRepositoryRole.Workspace);
            var sourceRows = pendingRows.Where(r => r != rootRow).ToList();
            Func<WorkspaceFeatureRepository, Task> createRowAsync = async row =>
            {
                var queuedAt = Stopwatch.GetTimestamp();
                await gate.WaitAsync(cancellationToken);
                var rowStartedAt = Stopwatch.GetTimestamp();
                var gateWaitMs = FeatureOperationLog.ElapsedMs(queuedAt);
                try
                {
                    var link = links.First(l => l.WorkspaceRepositoryId == row.WorkspaceRepositoryId);
                    var mainPath = await pathResolver.GetRepositoryPathAsync(
                        specialContextId, link.WorkspaceRepositoryId, cancellationToken);
                    var pathMs = FeatureOperationLog.ElapsedMs(rowStartedAt);
                    var workerStartedAt = Stopwatch.GetTimestamp();
                    var response = await workerBridge.SendCommandAsync(
                        WorkerHubMethods.CreateGitWorktree,
                        new
                        {
                            mainRepositoryPath = mainPath,
                            worktreePath = row.WorktreePath,
                            branchName = row.PinnedTag == null ? name : null,
                            detach = row.PinnedTag != null,
                            baseCommitSha = row.BaseCommitSha,
                            divergenceBaseBranch = row.ParentBranchName,
                            workspaceId,
                            repositoryId = link.RepositoryId,
                            capabilities
                        },
                        cancellationToken);
                    var workerMs = FeatureOperationLog.ElapsedMs(workerStartedAt);
                    var dbStartedAt = Stopwatch.GetTimestamp();

                    await using var writeDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                    var tracked = await writeDb.WorkspaceFeatureRepositories
                        .FirstAsync(r => r.WorkspaceFeatureRepositoryId == row.WorkspaceFeatureRepositoryId, cancellationToken);

                    if (!response.Success)
                    {
                        tracked.State = WorkspaceFeatureRepositoryState.NeedsRepair;
                        tracked.LastError = response.Error ?? "CreateGitWorktree failed.";
                        Interlocked.Exchange(ref anyFailure, 1);
                        logger.FeatureRepositoryFailed("Create", workspaceId, name, link.Repository?.RepositoryName, tracked.LastError);
                    }
                    else
                    {
                        var payload = WorkerResponseJson.DeserializeWorkerResponse<CreateGitWorktreeWorkerResponse>(response.Data);
                        if (payload is null || !payload.Success)
                        {
                            tracked.State = WorkspaceFeatureRepositoryState.NeedsRepair;
                            tracked.LastError = payload?.ErrorMessage ?? "CreateGitWorktree returned no payload.";
                            Interlocked.Exchange(ref anyFailure, 1);
                            logger.FeatureRepositoryFailed("Create", workspaceId, name, link.Repository?.RepositoryName, tracked.LastError);
                        }
                        else
                        {
                            if (!string.IsNullOrWhiteSpace(payload.WorktreePath))
                                tracked.WorktreePath = payload.WorktreePath;
                            tracked.State = WorkspaceFeatureRepositoryState.Ready;
                            tracked.LastError = null;
                            logger.FeatureRepositoryDone("Create", workspaceId, name, link.Repository?.RepositoryName, "Ready");
                        }
                    }

                    await writeDb.SaveChangesAsync(cancellationToken);
                    logger.FeatureRepositoryTiming(
                        "Create", workspaceId, name, link.Repository?.RepositoryName,
                        gateWaitMs, pathMs, workerMs,
                        FeatureOperationLog.ElapsedMs(dbStartedAt),
                        FeatureOperationLog.ElapsedMs(queuedAt),
                        tracked.State.ToString());
                }
                finally
                {
                    var done = Interlocked.Increment(ref createCompleted);
                    progress.Report(
                        $"Created feature in {done} of {createTotal} repositories",
                        done,
                        createTotal);
                    gate.Release();
                }
            };

            // D10: the Workspace-role root worktree is created alone and first; Source worktrees live
            // inside it. A root failure leaves every Source row Pending and skips the fan-out.
            if (rootRow != null)
                await createRowAsync(rootRow);
            stageStart = logger.FeatureStageFinished("Create", workspaceId, name, "RootWorktree", stageStart, rootRow == null ? "NoRoot" : "Root");
            if (anyFailure == 0)
                await Task.WhenAll(sourceRows.Select(createRowAsync));
            stageStart = logger.FeatureStageFinished(
                "Create", workspaceId, name, "SourceWorktrees", stageStart,
                $"Sources={sourceRows.Count} MaxParallel={MaxParallel} Failed={anyFailure}");

            await using (var finalizeDb = await dbContextFactory.CreateDbContextAsync(cancellationToken))
            {
                var trackedFeature = await finalizeDb.WorkspaceFeatures
                    .FirstAsync(f => f.WorkspaceFeatureId == feature.WorkspaceFeatureId, cancellationToken);
                if (anyFailure != 0)
                {
                    trackedFeature.LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair;
                    trackedFeature.LastError = "One or more worktrees failed to create.";
                    trackedFeature.UpdatedAt = DateTime.UtcNow;
                    await finalizeDb.SaveChangesAsync(cancellationToken);
                    return new CreateFeatureResult
                    {
                        Success = false,
                        Condition = "NeedsRepair",
                        Error = trackedFeature.LastError,
                        ContextId = contextId,
                        WorkspaceFeatureId = feature.WorkspaceFeatureId
                    };
                }

                progress?.Report(new OperationProgress("Seeding Feature projections..."));
                await SeedInitialFeatureProjectionsAsync(finalizeDb, workspaceId, contextId, name, cancellationToken);
                stageStart = logger.FeatureStageFinished("Create", workspaceId, name, "SeedProjections", stageStart);

                trackedFeature.LifecycleState = WorkspaceFeatureLifecycleState.Ready;
                trackedFeature.LastError = null;
                trackedFeature.UpdatedAt = DateTime.UtcNow;
                await finalizeDb.SaveChangesAsync(cancellationToken);
            }

            await selectedContextService.SetSelectedAsync(workspaceId, contextId, cancellationToken);
            await InitializeCodeGraphAsync(workspaceId, contextId, progress, cancellationToken);
            progress?.Report(new OperationProgress($"Feature '{name}' is ready."));
            return new CreateFeatureResult
            {
                Success = true,
                ContextId = contextId,
                WorkspaceFeatureId = feature.WorkspaceFeatureId
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Feature Create failed after the Feature was saved. WorkspaceId={WorkspaceId} FeatureName={FeatureName}", workspaceId, name);
            // Use CancellationToken.None: cancellationToken is very likely the thing that just got
            // cancelled (Abort), and this write must still land so the Feature does not stay stuck.
            await using var failDb = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            var trackedFeature = await failDb.WorkspaceFeatures
                .FirstAsync(f => f.WorkspaceFeatureId == feature.WorkspaceFeatureId, CancellationToken.None);
            trackedFeature.LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair;
            trackedFeature.LastError = DescribeOperationFailure(ex, "Create feature");
            trackedFeature.UpdatedAt = DateTime.UtcNow;
            await failDb.SaveChangesAsync(CancellationToken.None);

            return new CreateFeatureResult
            {
                Success = false,
                Condition = "NeedsRepair",
                Error = trackedFeature.LastError,
                ContextId = contextId,
                WorkspaceFeatureId = feature.WorkspaceFeatureId
            };
        }
    }

    public async Task<RemoveFeaturePlan> AnalyzeRemoveFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null)
    {
        var info = await contextResolver.GetRequiredAsync(featureContextId, cancellationToken: cancellationToken);
        if (info.IsSpecialWorkspace)
            return new RemoveFeaturePlan { Success = false, Error = "Cannot remove the special Workspace context." };

        var specialContextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(info.WorkspaceId, cancellationToken);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Include(r => r.WorkspaceRepository)!.ThenInclude(l => l!.Repository)
            // A Removed row's worktree is already unregistered; never analysed as if still live (D2).
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value
                && r.State != WorkspaceFeatureRepositoryState.Removed)
            .ToListAsync(cancellationToken);

        var states = await db.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == featureContextId.Value)
            .ToListAsync(cancellationToken);

        // Refresh PR state live for this Feature's own repos (never the shared Workspace link or
        // legacy PR row - see §17) before classifying, so "Completed" reflects today's GitHub state
        // rather than a stale cached row. A failed refresh for any repo makes PR state Unknown for
        // the whole analysis; it is never silently treated as "no pull request".
        var branchByRepositoryId = rows
            .Where(r => r.WorkspaceRepository != null)
            .ToDictionary(
                r => r.WorkspaceRepository!.RepositoryId,
                r => states.FirstOrDefault(s => s.WorkspaceRepositoryId == r.WorkspaceRepositoryId)?.BranchName
                    ?? (r.PinnedTag == null ? info.FeatureName : null));

        // PR refresh (GitHub) and Feature workspace path resolution are independent; run together
        // so the Checking overlay does not wait for them sequentially.
        var pullRequestRefreshTask = RefreshPullRequestsForRemoveAnalysisAsync(
            info.WorkspaceId, featureContextId.Value, branchByRepositoryId, cancellationToken);
        var pathArgsTask = ResolveFeatureWorkspaceArgsForRemoveAnalysisAsync(
            featureContextId, cancellationToken);
        await Task.WhenAll(pullRequestRefreshTask, pathArgsTask);

        var pullRequestStatusUnknown = await pullRequestRefreshTask;
        var (featureWorkspaceRoot, featureWorkspaceFolder, featureWorkspaceRepositoryName) = await pathArgsTask;

        var prs = await db.WorkspaceRepositoryContextPullRequests
            .AsNoTracking()
            .Where(p => p.WorkspaceFeatureContextId == featureContextId.Value)
            .ToListAsync(cancellationToken);

        var planSlots = new RemoveFeatureRepositoryPlan[rows.Count];
        var probeCompleted = 0;
        var probeTotal = rows.Count;
        using (var gate = new SemaphoreSlim(MaxParallel))
        {
            var probeTasks = rows.Select(async (row, index) =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var state = states.FirstOrDefault(s => s.WorkspaceRepositoryId == row.WorkspaceRepositoryId);
                    var pr = prs.FirstOrDefault(p => p.WorkspaceRepositoryId == row.WorkspaceRepositoryId);
                    var mainPath = await pathResolver.GetRepositoryPathAsync(
                        specialContextId, row.WorkspaceRepositoryId, cancellationToken);
                    // The Feature branch itself, not whatever happens to be checked out right now
                    // (09 SB-2): Remove always deletes this name, so the analysis must judge it, even
                    // when the worktree has drifted to another branch or a detached commit.
                    var featureBranchName = row.PinnedTag == null ? info.FeatureName : null;
                    var disk = await InspectWorktreeDiskStatusAsync(
                        mainPath, row.WorktreePath, row.WorkspaceRepository?.DefaultBranchName, featureBranchName, cancellationToken);
                    var live = await ProbeFeatureWorktreeLiveStatusAsync(
                        featureWorkspaceRoot,
                        featureWorkspaceFolder,
                        featureWorkspaceRepositoryName,
                        info.WorkspaceId,
                        row,
                        disk.Exists,
                        cancellationToken);

                    // The live current branch, from the Worker; null when detached or when disk status
                    // is itself Unknown (09 SB-2). Drift is judged against this, never against the
                    // cached database state, which can be stale.
                    var checkedOutBranch = disk.StatusUnknown ? null : disk.Branch;
                    var isOffFeatureBranch = featureBranchName != null
                        && !string.Equals(checkedOutBranch, featureBranchName, StringComparison.Ordinal);

                    planSlots[index] = new RemoveFeatureRepositoryPlan
                    {
                        WorkspaceRepositoryId = row.WorkspaceRepositoryId,
                        RepositoryName = row.WorkspaceRepository?.Repository?.RepositoryName ?? "",
                        WorktreePath = row.WorktreePath,
                        WorktreeExists = disk.Exists,
                        WorktreeStatusUnknown = disk.StatusUnknown,
                        BranchName = state?.BranchName ?? (row.PinnedTag == null ? info.FeatureName : null),
                        HeadCommit = live.HeadCommit ?? state?.HeadCommit,
                        HasUncommittedChanges = live.HasUncommittedChanges,
                        HasStagedChanges = live.HasStagedChanges,
                        HasConflicts = live.HasConflicts,
                        LiveStatusEstablished = live.LiveStatusEstablished,
                        OutgoingCommits = disk.StatusUnknown ? null : disk.AheadOfUpstream,
                        HasUpstream = disk.HasUpstream == true,
                        AheadOfDefault = disk.StatusUnknown ? null : disk.AheadOfDefault,
                        PullRequestNumber = pr?.PullRequestNumber,
                        PullRequestState = pr?.State,
                        PullRequestMerged = pr?.MergedAt is not null,
                        IsLocked = disk.IsLocked,
                        LockReason = disk.LockReason,
                        Warning = ComposeRemoveWarning(row, live, disk),
                        FeatureBranchName = featureBranchName,
                        CheckedOutBranch = checkedOutBranch,
                        IsOffFeatureBranch = isOffFeatureBranch,
                        FeatureBranchExists = disk.StatusUnknown ? null : disk.FeatureBranchExists,
                        FeatureBranchAheadOfDefault = disk.StatusUnknown ? null : disk.FeatureBranchAheadOfDefault,
                        FeatureBranchHasUpstream = disk.StatusUnknown ? null : disk.FeatureBranchHasUpstream,
                        FeatureBranchAheadOfUpstream = disk.StatusUnknown ? null : disk.FeatureBranchAheadOfUpstream,
                        FeatureBranchSha = disk.StatusUnknown ? null : disk.FeatureBranchSha
                    };
                }
                finally
                {
                    var done = Interlocked.Increment(ref probeCompleted);
                    progress.Report($"Checked {done} of {probeTotal}", done, probeTotal);
                    gate.Release();
                }
            });
            await Task.WhenAll(probeTasks);
        }

        var plans = planSlots.ToList();
        var classification = Classify(plans);
        var safe = IsAutomaticallySafe(classification, pullRequestStatusUnknown, plans);

        return new RemoveFeaturePlan
        {
            Success = true,
            Classification = classification,
            Repositories = plans,
            IsAutomaticallySafe = safe,
            PullRequestStatusUnknown = pullRequestStatusUnknown,
            Summary = $"{plans.Count} repositories; classification={classification}"
        };
    }

    private async Task<bool> RefreshPullRequestsForRemoveAnalysisAsync(
        int workspaceId,
        int featureContextId,
        IReadOnlyDictionary<int, string?> branchByRepositoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcomes = await workspacePullRequestService.RefreshContextPullRequestsAsync(
                workspaceId, featureContextId, branchByRepositoryId, force: false, cancellationToken);
            return outcomes.Values.Any(o => o == PullRequestRefreshOutcome.Failed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pull request refresh failed for Feature remove analysis.");
            return true;
        }
    }

    private async Task<(string? Root, string? Folder, string? WorkspaceRepositoryName)> ResolveFeatureWorkspaceArgsForRemoveAnalysisAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken)
    {
        try
        {
            var featureArgs = await pathResolver.GetWorkerArgsAsync(featureContextId, cancellationToken);
            return (featureArgs.WorkspaceRoot, featureArgs.WorkspaceFolderName, featureArgs.WorkspaceRepositoryName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve Feature workspace paths for remove analysis.");
            return (null, null, null);
        }
    }

    public async Task<OperationResult> RemoveFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        RemoveFeatureOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = await contextResolver.GetRequiredAsync(featureContextId, cancellationToken: cancellationToken);
        if (info.IsSpecialWorkspace)
            return OperationResult.Fail("Cannot remove the special Workspace context.");

        // Start the structural overlay immediately so the confirmation dialog does not sit idle while
        // analyze (or auth validation) runs. Prefer the plan already shown in the dialog when present.
        var tcs = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlayKey = WorkspaceJobKeys.RepositoriesOverlayKey(info.WorkspaceId);
        var started = operationLock.TryStartStructural(
            info.WorkspaceId,
            "remove-feature",
            overlayKey,
            "Removing feature...",
            async (op, ct) =>
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                    var overlayProgress = BindOverlayProgress(op, progress);

                    var plan = options.AnalyzedPlan is { Success: true } preAnalyzed
                        ? preAnalyzed
                        : await AnalyzeRemoveFeatureAsync(featureContextId, linked.Token);
                    if (!plan.Success)
                    {
                        tcs.TrySetResult(OperationResult.Fail(plan.Error ?? "Analyze failed."));
                        return;
                    }

                    // Unknown disk state (Worker unreachable, or InspectWorktree failed) can hide real dirty work,
                    // so Remove is refused here regardless of discard/force authorization - see A2 rule.
                    if (plan.Repositories.Any(r => r.WorktreeStatusUnknown))
                    {
                        tcs.TrySetResult(OperationResult.Fail(
                            "Could not check one or more repositories. Make sure the Worker is running, then try again."));
                        return;
                    }

                    // D3: require each authorization only when that risk is actually present (same rules as the
                    // Remove dialog checkboxes). Classification / PR-unknown / null outgoing / NeedsRepair alone
                    // make IsAutomaticallySafe false but do not need discard/force/unlock - Remove stays allowed
                    // (A3, I1). The old "any not-safe plan needs some authorization flag" gate rejected those
                    // cases even when the dialog correctly enabled Remove with no checkboxes shown.
                    var needsDiscard = plan.Repositories.Any(r =>
                        r.HasUncommittedChanges || r.HasStagedChanges || r.HasConflicts);
                    var needsForce = plan.Repositories.Any(r =>
                        (r.EffectiveAheadOfDefault ?? 0) > 0 && r.PullRequestMerged != true);
                    var needsUnlock = plan.Repositories.Any(r => r.IsLocked);

                    if (needsDiscard && !options.AllowDiscardUncommitted)
                    {
                        tcs.TrySetResult(OperationResult.Fail(
                            "This Feature has uncommitted changes; authorize discarding them explicitly."));
                        return;
                    }

                    if (needsForce && !options.AllowForceDeleteLocalBranches)
                    {
                        tcs.TrySetResult(OperationResult.Fail(
                            "This Feature has local branches with commits not in the default branch; authorize force-deleting them explicitly."));
                        return;
                    }

                    if (needsUnlock && !options.AllowUnlockWorktrees)
                    {
                        tcs.TrySetResult(OperationResult.Fail(
                            "This Feature has locked worktrees; authorize unlocking them explicitly."));
                        return;
                    }

                    var outcome = await RemoveFeatureCoreAsync(
                        featureContextId, info, options, plan, overlayProgress, linked.Token);
                    tcs.TrySetResult(outcome);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Feature Remove threw. WorkspaceId={WorkspaceId} FeatureName={FeatureName}", info.WorkspaceId, info.FeatureName);
                    tcs.TrySetResult(OperationResult.Fail(ex.Message));
                    throw;
                }
            },
            out var operation);

        if (!started)
        {
            logger.LogInformation(
                "Feature Remove refused because the Workspace is busy. WorkspaceId={WorkspaceId} FeatureName={FeatureName}",
                info.WorkspaceId, info.FeatureName);
            return OperationResult.Fail("A Workspace structural operation is already running.");
        }

        var startedAt = logger.FeatureOperationStarted(
            "Remove", info.WorkspaceId, info.WorkspaceFeatureId, featureContextId.Value, info.FeatureName);
        try
        {
            var removed = await tcs.Task.WaitAsync(cancellationToken);
            await operation.WhenCompleted;
            logger.FeatureOperationFinished(
                "Remove", info.WorkspaceId, info.WorkspaceFeatureId, featureContextId.Value, info.FeatureName, startedAt,
                removed.Success ? "Succeeded" : "Failed", removed.Error);
            return removed;
        }
        catch (OperationCanceledException)
        {
            logger.FeatureOperationFinished(
                "Remove", info.WorkspaceId, info.WorkspaceFeatureId, featureContextId.Value, info.FeatureName, startedAt, "Cancelled");
            throw;
        }
    }

    private async Task<OperationResult> RemoveFeatureCoreAsync(
        WorkspaceFeatureContextId featureContextId,
        WorkspaceFeatureContextInfo info,
        RemoveFeatureOptions options,
        RemoveFeaturePlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Drift (09 SB-2), from the plan already analysed just before this ran: which branch, if any,
        // the worktree was actually on instead of its Feature branch. Looked up by WorkspaceRepositoryId
        // below so the removal report can name the kept branch instead of only deleting silently.
        var driftByWrId = plan.Repositories.ToDictionary(r => r.WorkspaceRepositoryId, r => r);

        // D2: the periodic Git Changes sweep must never scan this context's worktrees while they are
        // being deleted below. Disposed in every case (success, failure, cancellation) so monitoring
        // always resumes; the special Workspace context is never paused, so its own sweep is unaffected.
        using var monitoringPause = gitChangesMonitoringPause.Pause(featureContextId.Value);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var feature = await db.WorkspaceFeatures
            .FirstAsync(f => f.WorkspaceFeatureId == info.WorkspaceFeatureId, cancellationToken);

        var specialContextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(info.WorkspaceId, cancellationToken);
        // Rows already Removed (a prior partial remove) are left alone here; only the still-live rows
        // move to Removing. Skipping them keeps a retry from re-asking the Worker about an already gone worktree.
        var rows = await db.WorkspaceFeatureRepositories
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value
                && r.State != WorkspaceFeatureRepositoryState.Removed)
            .ToListAsync(cancellationToken);

        feature.LifecycleState = WorkspaceFeatureLifecycleState.Removing;
        feature.UpdatedAt = DateTime.UtcNow;
        foreach (var row in rows)
            row.State = WorkspaceFeatureRepositoryState.Removing;
        await db.SaveChangesAsync(cancellationToken);

        // Before any worktree goes: CodeGraph's index (and a build still running) must not hold the Feature root.
        await UninitializeCodeGraphAsync(info.WorkspaceId, featureContextId, progress, cancellationToken);

        var wrIds = rows.Select(r => r.WorkspaceRepositoryId).ToList();
        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Include(l => l.Repository)!.ThenInclude(r => r!.Connector)
            .Where(l => wrIds.Contains(l.WorkspaceRepositoryId))
            .ToListAsync(cancellationToken);
        var linkByWrId = links.ToDictionary(l => l.WorkspaceRepositoryId);
        var repositoryIds = links.Select(l => l.RepositoryId).Distinct().ToList();

        var errorsByWrId = new ConcurrentDictionary<int, string>();
        var reportByWrId = new ConcurrentDictionary<int, RemoveFeatureRepositoryReport>();
        var removeCompleted = 0;
        var removeTotal = rows.Count;
        string? workspaceRoot = null;
        string? workspaceFolderName = null;
        string? workspaceRepositoryName = null;
        try
        {
            (workspaceRoot, workspaceFolderName, workspaceRepositoryName) =
                await pathResolver.GetWorkerArgsAsync(specialContextId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve Workspace worker paths for Feature branch delete.");
        }

        // D1's Worker-side residue cleanup only deletes files when both of these are set; an old App
        // (or a resolution failure here) leaves them null, matching the old, report-only behaviour.
        string? featureRootPath = null;
        string? featureStorageRoot = null;
        try
        {
            featureRootPath = await pathResolver.GetContextRootAsync(featureContextId, cancellationToken);
            (featureStorageRoot, _, _) = await pathResolver.GetWorkerArgsAsync(featureContextId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve Feature storage paths for worktree residue cleanup.");
        }

        // D10: the Workspace-role root worktree is removed last, alone, and only when every Source
        // removal succeeded (git worktree remove on a root that still holds Source worktrees would
        // fail or need --force).
        var rootRow = rows.SingleOrDefault(r =>
            linkByWrId.TryGetValue(r.WorkspaceRepositoryId, out var rootLink)
            && rootLink.Role == WorkspaceRepositoryRole.Workspace);
        var sourceRows = rows.Where(r => r != rootRow).ToList();
        var rootKeptMessage = false;

        using (var gate = new SemaphoreSlim(MaxParallel))
        {
            Func<WorkspaceFeatureRepository, Task> removeRowAsync = async row =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var repoName = linkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var reportLink)
                        ? reportLink.Repository?.RepositoryName ?? ""
                        : "";
                    var isRootRow = ReferenceEquals(row, rootRow);

                    var mainPath = await pathResolver.GetRepositoryPathAsync(
                        specialContextId, row.WorkspaceRepositoryId, cancellationToken);
                    // Force worktree remove only when the user authorized discarding dirty Feature files.
                    // AllowForceDeleteLocalBranches does not imply discard permission.
                    var force = options.AllowDiscardUncommitted;
                    var response = await workerBridge.SendCommandAsync(
                        WorkerHubMethods.RemoveGitWorktree,
                        new
                        {
                            mainRepositoryPath = mainPath,
                            worktreePath = row.WorktreePath,
                            force,
                            // The root removal deletes the directory itself; Worker residue cleanup must not run.
                            featureRootPath = isRootRow ? null : featureRootPath,
                            featureStorageRoot = isRootRow ? null : featureStorageRoot,
                            unlock = options.AllowUnlockWorktrees
                        },
                        cancellationToken);
                    if (!response.Success)
                    {
                        logger.LogWarning(
                            "RemoveGitWorktree failed for {Path}: {Error}. Repository={Repository} FeatureName={FeatureName}",
                            row.WorktreePath, FeatureOperationLog.Redact(response.Error), repoName, info.FeatureName);
                        errorsByWrId[row.WorkspaceRepositoryId] = string.IsNullOrWhiteSpace(response.Error)
                            ? $"Failed to remove worktree {row.WorktreePath}."
                            : response.Error;
                        return;
                    }

                    var worktreeResult = WorkerResponseJson.DeserializeWorkerResponse<RemoveGitWorktreeResult>(response.Data);

                    // The worktree is unregistered now, regardless of any kept branch or leftover files
                    // below; persist this row's progress at once with its own short-lived context, so a
                    // crash before the whole Feature finishes still keeps already-removed repos removed.
                    await using (var rowDb = await dbContextFactory.CreateDbContextAsync(cancellationToken))
                    {
                        var trackedRow = await rowDb.WorkspaceFeatureRepositories.FirstAsync(
                            r => r.WorkspaceFeatureRepositoryId == row.WorkspaceFeatureRepositoryId, cancellationToken);
                        trackedRow.State = WorkspaceFeatureRepositoryState.Removed;
                        trackedRow.LastError = null;
                        await rowDb.SaveChangesAsync(cancellationToken);
                    }

                    // §27.8 / D4: after worktree remove, optionally delete the Feature branch locally
                    // and/or remotely from the main repository. Tag-pinned repositories never got a
                    // Feature branch; a same-named branch there is not ours.
                    var branchName = row.PinnedTag == null ? info.FeatureName : null;
                    var planRow = driftByWrId.GetValueOrDefault(row.WorkspaceRepositoryId);
                    var hasRemoteUpstream = planRow?.FeatureBranchHasUpstream == true;
                    RemoveFeatureBranchOutcome branchOutcome;
                    string? branchMessage;
                    var remoteOutcome = RemoveFeatureRemoteBranchOutcome.NotApplicable;
                    string? remoteMessage = null;

                    if (string.IsNullOrWhiteSpace(branchName))
                    {
                        branchOutcome = RemoveFeatureBranchOutcome.NotApplicable;
                        branchMessage = null;
                    }
                    else if (!linkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var link)
                        || string.IsNullOrWhiteSpace(link.Repository?.RepositoryName)
                        || string.IsNullOrWhiteSpace(workspaceRoot)
                        || string.IsNullOrWhiteSpace(workspaceFolderName))
                    {
                        // Never silently skipped: the repository's local branch may still exist, so this
                        // is reported in the removal report rather than only logged (D2).
                        branchOutcome = RemoveFeatureBranchOutcome.Failed;
                        branchMessage = "Could not resolve repository details to delete the local branch.";
                        logger.LogWarning(
                            "Could not resolve repository details to delete local Feature branch {Branch} for WorkspaceRepository {WorkspaceRepositoryId}.",
                            branchName, row.WorkspaceRepositoryId);
                    }
                    else if (!options.DeleteLocalBranches)
                    {
                        branchOutcome = RemoveFeatureBranchOutcome.Kept;
                        branchMessage = "Local Feature branch kept (delete not selected).";
                    }
                    else
                    {
                        var deleteLocal = await workerBridge.SendCommandAsync(
                            "DeleteBranch",
                            new
                            {
                                workspaceName = workspaceFolderName,
                                repositoryName = repoName,
                                workspaceRepositoryName,
                                branchName,
                                isRemote = false,
                                force = options.AllowForceDeleteLocalBranches,
                                workspaceRoot
                            },
                            cancellationToken);
                        if (deleteLocal.Success)
                        {
                            branchOutcome = RemoveFeatureBranchOutcome.Deleted;
                            branchMessage = null;
                        }
                        else
                        {
                            branchMessage = string.IsNullOrWhiteSpace(deleteLocal.Error)
                                ? "Local branch delete failed."
                                : deleteLocal.Error;
                            // A non-force delete is refused by Git only for unmerged commits; a force
                            // delete that still fails is a real failure, not an expected refusal.
                            branchOutcome = options.AllowForceDeleteLocalBranches
                                ? RemoveFeatureBranchOutcome.Failed
                                : RemoveFeatureBranchOutcome.KeptUnmerged;
                            logger.LogWarning(
                                "Delete local Feature branch {Branch} failed for {Repo}: {Error}",
                                branchName, repoName, FeatureOperationLog.Redact(deleteLocal.Error));
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(branchName) && hasRemoteUpstream)
                    {
                        if (!options.DeleteRemoteBranches)
                        {
                            remoteOutcome = RemoveFeatureRemoteBranchOutcome.Kept;
                            remoteMessage = "Remote Feature branch kept (delete not selected).";
                        }
                        else if (!linkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var remoteLink)
                            || string.IsNullOrWhiteSpace(remoteLink.Repository?.RepositoryName)
                            || string.IsNullOrWhiteSpace(workspaceRoot)
                            || string.IsNullOrWhiteSpace(workspaceFolderName))
                        {
                            remoteOutcome = RemoveFeatureRemoteBranchOutcome.Failed;
                            remoteMessage = "Could not resolve repository details to delete the remote branch.";
                            logger.FeatureRepositoryFailed("Remove", info.WorkspaceId, info.FeatureName, repoName, remoteMessage);
                        }
                        else
                        {
                            var defaultBranch = remoteLink.DefaultBranchName;
                            if (!string.IsNullOrWhiteSpace(defaultBranch)
                                && branchName.Equals(defaultBranch.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                remoteOutcome = RemoveFeatureRemoteBranchOutcome.Failed;
                                remoteMessage = "Refused to delete the repository default branch.";
                                logger.FeatureRepositoryFailed("Remove", info.WorkspaceId, info.FeatureName, repoName, remoteMessage);
                            }
                            else
                            {
                                var expectedSha = planRow?.FeatureBranchSha;
                                if (string.IsNullOrWhiteSpace(expectedSha))
                                {
                                    remoteOutcome = RemoveFeatureRemoteBranchOutcome.Failed;
                                    remoteMessage = "Could not confirm the Feature branch tip for a safe remote delete.";
                                    logger.FeatureRepositoryFailed("Remove", info.WorkspaceId, info.FeatureName, repoName, remoteMessage);
                                }
                                else
                                {
                                    var bearerToken = ConnectorHelpers.UnprotectToken(
                                        remoteLink.Repository?.Connector?.UserToken);
                                    var deleteRemote = await workerBridge.SendCommandAsync(
                                        "DeleteBranch",
                                        new
                                        {
                                            workspaceName = workspaceFolderName,
                                            repositoryName = repoName,
                                            workspaceRepositoryName,
                                            branchName,
                                            isRemote = true,
                                            force = false,
                                            bearerToken,
                                            expectedSha,
                                            workspaceRoot
                                        },
                                        cancellationToken);
                                    if (deleteRemote.Success)
                                    {
                                        remoteOutcome = RemoveFeatureRemoteBranchOutcome.Deleted;
                                        remoteMessage = null;
                                    }
                                    else
                                    {
                                        var err = string.IsNullOrWhiteSpace(deleteRemote.Error)
                                            ? "Remote branch delete failed."
                                            : deleteRemote.Error;
                                        remoteOutcome = err.Contains("no longer matches", StringComparison.OrdinalIgnoreCase)
                                            || err.Contains("lease", StringComparison.OrdinalIgnoreCase)
                                            ? RemoveFeatureRemoteBranchOutcome.RefusedLease
                                            : RemoveFeatureRemoteBranchOutcome.Failed;
                                        remoteMessage = err;
                                        logger.LogWarning(
                                            "Delete remote Feature branch {Branch} failed for {Repo}: {Error}",
                                            branchName, repoName, FeatureOperationLog.Redact(deleteRemote.Error));
                                    }
                                }
                            }
                        }
                    }

                    // 09 SB-2: when the worktree was not on its Feature branch, name the branch that was
                    // actually kept (or "(detached commit)" when there was none) so the report never
                    // only says "removed" while leaving an unmerged branch silently behind.
                    var keptBranchName = planRow is { IsOffFeatureBranch: true }
                        ? planRow.CheckedOutBranch ?? "(detached commit)"
                        : null;

                    var residueRemaining = worktreeResult?.ResidueRemaining ?? false;
                    reportByWrId[row.WorkspaceRepositoryId] = new RemoveFeatureRepositoryReport(
                        row.WorkspaceRepositoryId,
                        repoName,
                        WorktreeRemoved: true,
                        branchOutcome,
                        branchMessage,
                        residueRemaining,
                        worktreeResult?.ResidueFileCount ?? 0,
                        worktreeResult?.ResidueSampleFiles,
                        worktreeResult?.ResidueMessage,
                        keptBranchName,
                        remoteOutcome,
                        remoteMessage);
                    logger.FeatureRepositoryDone("Remove", info.WorkspaceId, info.FeatureName, repoName, "WorktreeRemoved");
                }
                finally
                {
                    var done = Interlocked.Increment(ref removeCompleted);
                    progress.Report(
                        $"Removed feature from {done} of {removeTotal} repositories",
                        done,
                        removeTotal);
                    gate.Release();
                }
            };

            await Task.WhenAll(sourceRows.Select(removeRowAsync));
            if (rootRow != null)
            {
                if (errorsByWrId.IsEmpty)
                    await removeRowAsync(rootRow);
                else
                    rootKeptMessage = true;
            }
        }

        if (!errorsByWrId.IsEmpty)
        {
            // Worker work ran in parallel; apply EF updates sequentially (DbContext is not thread-safe).
            // Do not continue into Workspace refresh or report success: the UI would navigate back while
            // the Feature still owns failed worktrees. Keep metadata for retry (§27.8).
            // A failed row stays Removing with its error in LastError (D2 step 0): row-level NeedsRepair
            // is reserved for the sync-time "worktree missing" case, not a failed Remove.
            foreach (var row in rows)
            {
                if (!errorsByWrId.TryGetValue(row.WorkspaceRepositoryId, out var error))
                    continue;
                row.LastError = error;
            }

            var firstError = errorsByWrId.Values.First();
            if (rootKeptMessage)
            {
                const string rootKept = "Workspace repository worktree kept until all repository worktrees are removed.";
                firstError = $"{firstError} {rootKept}";
                rootRow!.LastError = rootKept;
            }

            feature.LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair;
            feature.LastError = firstError;
            feature.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return OperationResult.Fail(firstError);
        }

        // Every worktree is unregistered now. Anything still on disk (a file or folder some program has open) is deleted
        // here if it has let go, or marked pending deletion for the background cleanup; Remove itself never waits on it.
        var pendingDeletionFolder = rootRow != null || reportByWrId.Values.Any(r => r.ResidueRemaining)
            ? await CleanupFeatureFolderForNameAsync(info.WorkspaceId, info.FeatureName, onlyIfMarked: false, cancellationToken)
            : null;

        // Refresh special Workspace snapshot before dropping Feature rows so the grid is not stale
        // after navigation back to Workspace. ParentBranchName / SourceBranchName is provenance only
        // and must never drive a checkout or return-to-default here.
        await RefreshWorkspaceStateAfterFeatureRemoveAsync(
            info.WorkspaceId,
            specialContextId,
            repositoryIds,
            progress,
            cancellationToken);

        var selected = await selectedContextService.GetSelectedAsync(info.WorkspaceId, cancellationToken);
        if (selected?.Value == featureContextId.Value)
            await selectedContextService.SetSelectedAsync(info.WorkspaceId, specialContextId, cancellationToken);

        // Deletes every row for this context, including any already-Removed row from an earlier
        // partial remove that was excluded from `rows` above - the whole Feature is gone now.
        await db.WorkspaceFeatureRepositories
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
            .ExecuteDeleteAsync(cancellationToken);

        // ExecuteDeleteAsync bypasses the change tracker, so `rows` (still tracked as Unchanged
        // children of `context`) must be detached here. Otherwise removing `context` below makes EF
        // cascade-delete these already-gone rows again, and that second delete affects 0 rows.
        foreach (var row in rows)
            db.Entry(row).State = EntityState.Detached;

        var context = await db.WorkspaceFeatureContexts
            .FirstAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value, cancellationToken);

        // Feature-context-scoped data (WorkspaceProjects, their dependencies, file-line statuses) is
        // owned solely by this context and must be dropped with it. Never run this for the special
        // Workspace context: those rows are shared and have no context id to isolate them.
        if (context.Kind == WorkspaceFeatureContextKind.Feature)
            await DeleteContextScopedProjectDataAsync(db, featureContextId.Value, cancellationToken);

        db.WorkspaceFeatureContexts.Remove(context);
        db.WorkspaceFeatures.Remove(feature);
        await db.SaveChangesAsync(cancellationToken);

        var report = rows
            .Select(r => reportByWrId.TryGetValue(r.WorkspaceRepositoryId, out var entry) ? entry : null)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToList();
        return OperationResult.Ok() with
        {
            RemoveFeatureReport = report,
            RemoveFeaturePendingDeletionFolder = pendingDeletionFolder,
        };
    }

    /// <summary>
    /// Deletes the folder of a Feature that no longer has worktrees (<c>{ManagedFeatureStorageRoot}\{featureName}</c>), or
    /// has the Worker mark it pending deletion while something still holds it. With <paramref name="onlyIfMarked"/>, only a
    /// folder already marked pending deletion is touched. Returns the folder when it was left marked; null when it is gone,
    /// was not ours to touch, the Workspace has no managed storage root, or the Worker could not be asked.
    /// </summary>
    private async Task<string?> CleanupFeatureFolderForNameAsync(
        int workspaceId,
        string? featureName,
        bool onlyIfMarked,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(featureName))
            return null;

        string? storageRoot;
        string workspaceName;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var workspace = await db.Workspaces.AsNoTracking()
                .Where(w => w.WorkspaceId == workspaceId)
                .Select(w => new { w.Name, w.ManagedFeatureStorageRoot })
                .FirstOrDefaultAsync(cancellationToken);
            storageRoot = workspace?.ManagedFeatureStorageRoot;
            workspaceName = workspace?.Name ?? "";
        }

        // Never touch the legacy drive-root location; GrayMoon only cleans folders under its managed storage root.
        if (string.IsNullOrWhiteSpace(storageRoot) || WorkerPath.IsLegacyWindowsDriveRootGraymoonPath(storageRoot))
            return null;

        var featureRootPath = WorkerPath.Combine(storageRoot, featureName);
        var outcome = await CleanupFeatureFolderAsync(storageRoot, featureRootPath, workspaceName, featureName, onlyIfMarked, cancellationToken);
        return outcome == CleanupOutcomePendingDeletion ? featureRootPath : null;
    }

    /// <summary>
    /// Asks the Worker to start a CodeGraph index for a Feature that just became Ready, when the Workspace definition
    /// turns CodeGraph on and the Workspace root has an index. Optional: never throws, never fails the operation; the
    /// Feature is already Ready, so not even an Abort here may turn it into a failure.
    /// </summary>
    private async Task InitializeCodeGraphAsync(
        int workspaceId,
        WorkspaceFeatureContextId featureContextId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (codeGraph is null)
            return;

        try
        {
            await codeGraph.InitializeAsync(workspaceId, featureContextId, progress, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CodeGraph init failed. WorkspaceId={WorkspaceId} ContextId={ContextId}", workspaceId, featureContextId.Value);
        }
    }

    /// <summary>
    /// Removes the Feature root's CodeGraph index before its worktrees are removed. A failure is logged only: the Feature
    /// folder goes anyway, and a file still held open leaves it pending deletion like any other.
    /// </summary>
    private async Task UninitializeCodeGraphAsync(
        int workspaceId,
        WorkspaceFeatureContextId featureContextId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (codeGraph is null)
            return;

        try
        {
            await codeGraph.UninitializeAsync(workspaceId, featureContextId, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "CodeGraph uninit failed. WorkspaceId={WorkspaceId} ContextId={ContextId}", workspaceId, featureContextId.Value);
        }
    }

    /// <summary>
    /// App-side shape of the Worker's RemoveGitWorktree response (GrayMoon.Worker.Jobs.Response is not
    /// referenced here), used only to read the residue fields added for the Remove report (D1/D2).
    /// </summary>
    private sealed class RemoveGitWorktreeResult
    {
        public bool Success { get; set; }
        public bool ResidueRemaining { get; set; }
        public int ResidueFileCount { get; set; }
        public List<string>? ResidueSampleFiles { get; set; }
        public string? ResidueMessage { get; set; }
        public string? FailureKind { get; set; }
    }

    /// <summary>App-side shape of the Worker's CleanupFeatureFolder response.</summary>
    private sealed class CleanupFeatureFolderResult
    {
        public string? Outcome { get; set; }
        public int RemainingFileCount { get; set; }
        public string? Message { get; set; }
    }

    private const string CleanupOutcomeRemoved = "Removed";
    private const string CleanupOutcomePendingDeletion = "PendingDeletion";

    /// <summary>
    /// Asks the Worker to delete a Feature's leftover folder, or mark it pending deletion when files are still in use.
    /// Returns the Worker's outcome name, or null when the Worker could not be asked (logged; never thrown).
    /// </summary>
    private async Task<string?> CleanupFeatureFolderAsync(
        string featureStorageRoot,
        string featureRootPath,
        string workspaceName,
        string featureName,
        bool onlyIfMarked,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await workerBridge.SendCommandAsync(
                WorkerHubMethods.CleanupFeatureFolder,
                new
                {
                    featureStorageRoot,
                    featureRootPath,
                    workspaceName,
                    featureName,
                    retry = true,
                    onlyIfMarked
                },
                cancellationToken);
            var result = response.Success
                ? WorkerResponseJson.DeserializeWorkerResponse<CleanupFeatureFolderResult>(response.Data)
                : null;
            if (result?.Outcome is null)
            {
                logger.LogWarning(
                    "CleanupFeatureFolder failed for {FeatureRootPath}: {Error}",
                    featureRootPath, FeatureOperationLog.Redact(response.Error));
                return null;
            }

            if (result.Outcome != CleanupOutcomeRemoved)
            {
                logger.LogInformation(
                    "Feature folder {FeatureRootPath} left behind: {Outcome} ({Count} file(s)). {Message}",
                    featureRootPath, result.Outcome, result.RemainingFileCount, result.Message);
            }

            return result.Outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "CleanupFeatureFolder failed for {FeatureRootPath}", featureRootPath);
            return null;
        }
    }

    /// <summary>
    /// Deletes this Feature context's WorkspaceProjects, their ProjectDependencies and its
    /// WorkspaceFileLineStatuses. Caller guarantees <paramref name="featureContextId"/> is a Feature
    /// context, never the special Workspace context.
    /// </summary>
    private static async Task DeleteContextScopedProjectDataAsync(
        AppDbContext db,
        int featureContextId,
        CancellationToken cancellationToken)
    {
        var contextProjectIds = await db.WorkspaceProjects
            .Where(p => p.WorkspaceFeatureContextId == featureContextId)
            .Select(p => p.ProjectId)
            .ToListAsync(cancellationToken);

        if (contextProjectIds.Count > 0)
        {
            await db.ProjectDependencies
                .Where(d => contextProjectIds.Contains(d.DependentProjectId)
                    || contextProjectIds.Contains(d.ReferencedProjectId))
                .ExecuteDeleteAsync(cancellationToken);

            await db.WorkspaceProjects
                .Where(p => p.WorkspaceFeatureContextId == featureContextId)
                .ExecuteDeleteAsync(cancellationToken);
        }

        await db.WorkspaceFileLineStatuses
            .Where(s => s.WorkspaceFeatureContextId == featureContextId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// After Feature worktrees are gone, observe and persist the special Workspace checkout that
    /// already exists. Never checkout, switch branch, pull, or restore <c>ParentBranchName</c>.
    /// Refresh failure is logged only - Feature Git resources were already removed successfully.
    /// </summary>
    private async Task RefreshWorkspaceStateAfterFeatureRemoveAsync(
        int workspaceId,
        WorkspaceFeatureContextId specialContextId,
        IReadOnlyList<int> repositoryIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (repositoryIds.Count == 0)
            return;

        progress?.Report(new OperationProgress("Refreshing Workspace repository status..."));

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            await git.SyncAsync(
                workspaceId,
                specialContextId,
                repositoryIds: repositoryIds,
                cancellationToken: cancellationToken);
            await git.RecomputeAndBroadcastWorkspaceSyncedAsync(workspaceId, specialContextId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Non-mutating Workspace status refresh after Remove Feature failed for workspace {WorkspaceId}. Feature resources were already removed; a later Sync can recover.",
                workspaceId);
        }
    }

    private async Task<FeatureWorktreeLiveStatus> ProbeFeatureWorktreeLiveStatusAsync(
        string? featureWorkspaceRoot,
        string? featureWorkspaceFolder,
        string? workspaceRepositoryName,
        int workspaceId,
        WorkspaceFeatureRepository row,
        bool worktreeExists,
        CancellationToken cancellationToken)
    {
        if (!worktreeExists)
            return FeatureWorktreeLiveStatus.Unavailable;

        var repoName = row.WorkspaceRepository?.Repository?.RepositoryName;
        var repositoryId = row.WorkspaceRepository?.RepositoryId;
        if (string.IsNullOrWhiteSpace(featureWorkspaceRoot)
            || string.IsNullOrWhiteSpace(featureWorkspaceFolder)
            || string.IsNullOrWhiteSpace(repoName)
            || repositoryId is null
            || !workerBridge.IsWorkerConnected)
        {
            return FeatureWorktreeLiveStatus.Unavailable;
        }

        try
        {
            var capabilities = await capabilitiesResolver.GetAsync(workspaceId, cancellationToken);
            var response = await workerBridge.SendCommandAsync(
                "GetGitChangeStatus",
                new
                {
                    workspaceRoot = featureWorkspaceRoot,
                    workspaceName = featureWorkspaceFolder,
                    repositoryName = repoName,
                    workspaceRepositoryName,
                    workspaceId,
                    repositoryId = repositoryId.Value,
                    includeLineStats = false,
                    capabilities = capabilities.ToRepositoryOperationCapabilities()
                },
                cancellationToken);

            var status = WorkerResponseJson.DeserializeWorkerResponse<GitChangesStatusResult>(response.Data);
            if (status is { Success: false, ErrorCode: "PathUnderRemoval" })
            {
                // Expected: the Worker is removing this folder and refuses to start work in it. Not a failure.
                logger.LogDebug("GetGitChangeStatus skipped for Feature worktree {Path}: it is being removed", row.WorktreePath);
                return FeatureWorktreeLiveStatus.Unavailable;
            }

            if (status is null || !status.Success || status.Snapshot is null)
            {
                logger.LogWarning(
                    "GetGitChangeStatus failed for Feature worktree {Path}: {Error}",
                    row.WorktreePath,
                    status?.ErrorMessage ?? response.Error ?? "unknown");
                return FeatureWorktreeLiveStatus.Unavailable;
            }

            var snapshot = status.Snapshot;
            var hasUncommitted = snapshot.Changes.Any(c => c.IsChanged || c.WorktreeChange == GitChangeKind.Untracked);
            var hasStaged = snapshot.Changes.Any(c => c.IsStaged);
            var hasConflicts = snapshot.Changes.Any(c => c.IsConflicted)
                               || snapshot.IsMerging
                               || snapshot.IsRebasing
                               || snapshot.IsCherryPicking;

            return new FeatureWorktreeLiveStatus(
                LiveStatusEstablished: true,
                HasUncommittedChanges: hasUncommitted,
                HasStagedChanges: hasStaged,
                HasConflicts: hasConflicts,
                HeadCommit: snapshot.HeadCommit);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live Feature worktree status probe failed for {Path}", row.WorktreePath);
            return FeatureWorktreeLiveStatus.Unavailable;
        }
    }

    private static string? ComposeRemoveWarning(WorkspaceFeatureRepository row, FeatureWorktreeLiveStatus live, WorktreeDiskStatus disk)
    {
        if (row.State == WorkspaceFeatureRepositoryState.NeedsRepair && !string.IsNullOrWhiteSpace(row.LastError))
            return row.LastError;
        if (disk.StatusUnknown)
            return disk.UnknownReason ?? "Could not check this repository. Make sure the Worker is running, then try again.";
        if (!live.LiveStatusEstablished && disk.Exists)
            return "Live worktree status could not be established";
        return null;
    }

    private static bool IsAutomaticallySafe(
        RemoveFeatureClassification classification,
        bool pullRequestStatusUnknown,
        IReadOnlyList<RemoveFeatureRepositoryPlan> plans) =>
        classification is RemoveFeatureClassification.Completed
        && !pullRequestStatusUnknown
        && plans.All(p =>
            !p.WorktreeStatusUnknown
            && p.LiveStatusEstablished
            // The branch Remove actually deletes is the Feature branch, not whatever is checked out
            // right now (09 SB-2); EffectiveOutgoingCommits reads the Feature branch's own count for a
            // non-pinned repo. A null count (Worker unreachable, no upstream, or an older Worker) is
            // unknown, never treated as zero commits pending.
            && p.EffectiveOutgoingCommits == 0
            && !p.HasUncommittedChanges
            && !p.HasStagedChanges
            && !p.HasConflicts
            // A locked worktree (D5) must be explained and unlocked with consent, never removed silently.
            && !p.IsLocked);

    /// <summary>
    /// Disk facts for one Feature worktree, from the Worker's InspectWorktree command. The App never
    /// reads repository or worktree paths from local disk directly (it can run in Docker, where those
    /// paths do not exist). Any failure to reach the Worker or parse its response is Unknown, never
    /// treated as Missing.
    /// </summary>
    private async Task<WorktreeDiskStatus> InspectWorktreeDiskStatusAsync(
        string? mainRepositoryPath,
        string? worktreePath,
        string? defaultBranch,
        string? featureBranch,
        CancellationToken cancellationToken)
    {
        const string unknownReason = "Could not check this repository. Make sure the Worker is running, then try again.";

        if (string.IsNullOrWhiteSpace(mainRepositoryPath)
            || string.IsNullOrWhiteSpace(worktreePath)
            || !workerBridge.IsWorkerConnected)
        {
            return WorktreeDiskStatus.Unknown(unknownReason);
        }

        try
        {
            var response = await workerBridge.SendCommandAsync(
                WorkerHubMethods.InspectWorktree,
                new { mainRepositoryPath, worktreePath, defaultBranch, featureBranch },
                cancellationToken);
            if (!response.Success)
                return WorktreeDiskStatus.Unknown(unknownReason);

            var payload = WorkerResponseJson.DeserializeWorkerResponse<InspectWorktreeWorkerResponse>(response.Data);
            if (payload is null || !string.IsNullOrWhiteSpace(payload.Error))
                return WorktreeDiskStatus.Unknown(unknownReason);

            return WorktreeDiskStatus.Known(
                payload.Exists, payload.IsDirty, payload.HasUpstream, payload.AheadOfUpstream, payload.AheadOfDefault,
                payload.IsLocked, payload.LockReason, payload.Branch,
                payload.FeatureBranchExists, payload.FeatureBranchAheadOfDefault,
                payload.FeatureBranchHasUpstream, payload.FeatureBranchAheadOfUpstream,
                payload.FeatureBranchSha);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "InspectWorktree failed for {Path}", worktreePath);
            return WorktreeDiskStatus.Unknown(unknownReason);
        }
    }

    private readonly record struct FeatureWorktreeLiveStatus(
        bool LiveStatusEstablished,
        bool HasUncommittedChanges,
        bool HasStagedChanges,
        bool HasConflicts,
        string? HeadCommit)
    {
        public static FeatureWorktreeLiveStatus Unavailable { get; } = new(false, false, false, false, null);
    }

    public async Task<IReadOnlyList<WorkspaceFeatureContextInfo>> ListFeaturesAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        var all = await contextResolver.ListForWorkspaceAsync(workspaceId, cancellationToken);
        return all.Where(c => !c.IsSpecialWorkspace).ToList();
    }

    public async Task<bool> IsRemoveIncompleteAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkspaceFeatureRepositories.AnyAsync(
            r => r.WorkspaceFeatureContextId == featureContextId.Value
                && (r.State == WorkspaceFeatureRepositoryState.Removing
                    || r.State == WorkspaceFeatureRepositoryState.Removed),
            cancellationToken);
    }

    public async Task<FeatureStatusSnapshot?> GetFeatureStatusAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var context = await db.WorkspaceFeatureContexts
            .AsNoTracking()
            .Include(c => c.WorkspaceFeature)
            .FirstOrDefaultAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value, cancellationToken);
        if (context?.WorkspaceFeature is null)
            return null;

        var rows = await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
            .Include(r => r.WorkspaceRepository)!.ThenInclude(l => l!.Repository)
            .OrderBy(r => r.WorkspaceFeatureRepositoryId)
            .ToListAsync(cancellationToken);

        var repositories = rows
            .Select(r => new FeatureStatusRepository(
                r.WorkspaceRepository?.Repository?.RepositoryName ?? $"repo-{r.WorkspaceRepositoryId}",
                r.State.ToString(),
                r.LastError))
            .ToList();
        var removeIncomplete = rows.Any(r =>
            r.State is WorkspaceFeatureRepositoryState.Removing or WorkspaceFeatureRepositoryState.Removed);

        return new FeatureStatusSnapshot(
            context.WorkspaceFeature.Name,
            context.WorkspaceFeature.LastError,
            removeIncomplete,
            repositories);
    }

    /// <summary>
    /// Friendlier text than the raw "A task was canceled."/"The operation was canceled." messages .NET's
    /// task cancellation produces, for when the user presses the overlay's Abort button mid Repair/Roll back.
    /// </summary>
    private static string DescribeOperationFailure(Exception ex, string actionVerb) =>
        ex is OperationCanceledException ? $"{actionVerb} was cancelled." : DescribeFailureMessage(ex);

    /// <summary>
    /// EF wraps the useful cause ("UNIQUE constraint failed: ...") behind "An error occurred while saving the entity
    /// changes", so surface the innermost message of a save failure instead of the generic wrapper.
    /// </summary>
    internal static string DescribeFailureMessage(Exception ex)
    {
        if (ex is not DbUpdateException)
            return ex.Message;

        var inner = ex;
        while (inner.InnerException is not null)
            inner = inner.InnerException;

        return ReferenceEquals(inner, ex) || string.IsNullOrWhiteSpace(inner.Message)
            ? ex.Message
            : $"{ex.Message} {inner.Message}";
    }

    public async Task<RepairFeatureResult> RepairFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = await contextResolver.GetRequiredAsync(featureContextId, cancellationToken: cancellationToken);
        if (info.IsSpecialWorkspace || info.WorkspaceFeatureId is null)
            return new RepairFeatureResult(false, "Not a Feature context.", []);

        if (await IsRemoveIncompleteAsync(featureContextId, cancellationToken))
            return new RepairFeatureResult(false, "This Feature was being removed. Use Continue removal.", []);

        var tcs = new TaskCompletionSource<RepairFeatureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = operationLock.TryStartStructural(
            info.WorkspaceId,
            "repair-feature",
            WorkspaceJobKeys.RepositoriesOverlayKey(info.WorkspaceId),
            "Repairing feature...",
            async (op, ct) =>
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                    var result = await RepairFeatureCoreAsync(featureContextId, info, BindOverlayProgress(op, progress), linked.Token);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Feature Repair threw. WorkspaceId={WorkspaceId} FeatureName={FeatureName}", info.WorkspaceId, info.FeatureName);
                    tcs.TrySetResult(new RepairFeatureResult(false, DescribeOperationFailure(ex, "Repair"), []));
                    throw;
                }
            },
            out var operation);

        if (!started)
        {
            logger.LogInformation(
                "Feature Repair refused because the Workspace is busy. WorkspaceId={WorkspaceId} FeatureName={FeatureName}",
                info.WorkspaceId, info.FeatureName);
            return new RepairFeatureResult(false, "A Workspace structural operation is already running.", []);
        }

        var startedAt = logger.FeatureOperationStarted(
            "Repair", info.WorkspaceId, info.WorkspaceFeatureId, featureContextId.Value, info.FeatureName);
        try
        {
            var repaired = await tcs.Task.WaitAsync(cancellationToken);
            await operation.WhenCompleted;
            logger.FeatureOperationFinished(
                "Repair", info.WorkspaceId, info.WorkspaceFeatureId, featureContextId.Value, info.FeatureName, startedAt,
                repaired.Success ? "Succeeded" : "Failed", repaired.Error);
            return repaired;
        }
        catch (OperationCanceledException)
        {
            logger.FeatureOperationFinished(
                "Repair", info.WorkspaceId, info.WorkspaceFeatureId, featureContextId.Value, info.FeatureName, startedAt, "Cancelled");
            throw;
        }
    }

    public async Task<RollbackFeatureResult> RollbackFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = await contextResolver.GetRequiredAsync(featureContextId, cancellationToken: cancellationToken);
        if (info.IsSpecialWorkspace || info.WorkspaceFeatureId is null)
            return new RollbackFeatureResult(false, "Not a Feature context.", []);

        if (await IsRemoveIncompleteAsync(featureContextId, cancellationToken))
            return new RollbackFeatureResult(false, "This Feature was being removed. Use Continue removal.", []);

        // The dirty check (CollectDirtyReposForRollbackAsync) asks the Worker for live disk status per
        // repository, one at a time - for a Feature with many repositories this can take a few seconds
        // with nothing on screen if run before the structural lock starts (TryStartStructural raises the
        // page's BackgroundJobOverlay synchronously, before returning). Running it as the first step inside
        // the lock instead means "Rolling back feature..." is already showing by the time it runs.
        var tcs = new TaskCompletionSource<RollbackFeatureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = operationLock.TryStartStructural(
            info.WorkspaceId,
            "rollback-feature",
            WorkspaceJobKeys.RepositoriesOverlayKey(info.WorkspaceId),
            "Rolling back feature...",
            async (op, ct) =>
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                    var dirtyRefuse = await CollectDirtyReposForRollbackAsync(featureContextId, info, linked.Token);
                    if (dirtyRefuse is not null)
                    {
                        tcs.TrySetResult(dirtyRefuse);
                        return;
                    }

                    var result = await RollbackFeatureCoreAsync(featureContextId, info, BindOverlayProgress(op, progress), linked.Token);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Feature Rollback threw. WorkspaceId={WorkspaceId} FeatureName={FeatureName}", info.WorkspaceId, info.FeatureName);
                    tcs.TrySetResult(new RollbackFeatureResult(false, DescribeOperationFailure(ex, "Roll back"), []));
                    throw;
                }
            },
            out var operation);

        if (!started)
        {
            logger.LogInformation(
                "Feature Rollback refused because the Workspace is busy. WorkspaceId={WorkspaceId} FeatureName={FeatureName}",
                info.WorkspaceId, info.FeatureName);
            return new RollbackFeatureResult(false, "A Workspace structural operation is already running.", []);
        }

        var startedAt = logger.FeatureOperationStarted(
            "Rollback", info.WorkspaceId, info.WorkspaceFeatureId, featureContextId.Value, info.FeatureName);
        try
        {
            var rolledBack = await tcs.Task.WaitAsync(cancellationToken);
            await operation.WhenCompleted;
            logger.FeatureOperationFinished(
                "Rollback", info.WorkspaceId, info.WorkspaceFeatureId, featureContextId.Value, info.FeatureName, startedAt,
                rolledBack.Success ? "Succeeded" : "Failed", rolledBack.Error);
            return rolledBack;
        }
        catch (OperationCanceledException)
        {
            logger.FeatureOperationFinished(
                "Rollback", info.WorkspaceId, info.WorkspaceFeatureId, featureContextId.Value, info.FeatureName, startedAt, "Cancelled");
            throw;
        }
    }

    private async Task<RepairFeatureResult> RepairFeatureCoreAsync(
        WorkspaceFeatureContextId featureContextId,
        WorkspaceFeatureContextInfo info,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var featureName = info.FeatureName
            ?? throw new InvalidOperationException("Feature has no name.");
        var specialContextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(info.WorkspaceId, cancellationToken);

        // Same barrier as Create: syncs the Worker releases for retried worktrees wait for the seed below.
        using var finalizationBarrier = finalizationCoordinator?.Arm(featureContextId.Value);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.WorkspaceFeatureRepositories
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
            .ToListAsync(cancellationToken);
        var wrIds = rows.Select(r => r.WorkspaceRepositoryId).ToList();
        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Include(l => l.Repository)
            .Where(l => wrIds.Contains(l.WorkspaceRepositoryId))
            .ToListAsync(cancellationToken);
        var linkByWrId = links.ToDictionary(l => l.WorkspaceRepositoryId);

        var results = new ConcurrentBag<(int WrId, FeatureRepairRepositoryResult Result)>();
        var retryRows = rows
            .Where(r => r.State is WorkspaceFeatureRepositoryState.Pending or WorkspaceFeatureRepositoryState.NeedsRepair)
            .ToList();

        foreach (var row in rows.Where(r => r.State == WorkspaceFeatureRepositoryState.Ready))
        {
            var name = linkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var link)
                ? link.Repository?.RepositoryName ?? ""
                : "";
            results.Add((row.WorkspaceRepositoryId, new FeatureRepairRepositoryResult(name, FeatureRepositoryOperationOutcome.Skipped, "Already ready.")));
        }

        if (retryRows.Count > 0)
        {
            var capabilities = (await capabilitiesResolver.GetAsync(info.WorkspaceId, cancellationToken)).ToRepositoryOperationCapabilities();
            using var gate = new SemaphoreSlim(MaxParallel);
            var done = 0;
            Func<WorkspaceFeatureRepository, Task> retryRowAsync = async row =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var link = linkByWrId[row.WorkspaceRepositoryId];
                    var repoName = link.Repository?.RepositoryName ?? "";
                    var mainPath = await pathResolver.GetRepositoryPathAsync(
                        specialContextId, row.WorkspaceRepositoryId, cancellationToken);
                    var response = await workerBridge.SendCommandAsync(
                        WorkerHubMethods.CreateGitWorktree,
                        new
                        {
                            mainRepositoryPath = mainPath,
                            worktreePath = row.WorktreePath,
                            branchName = row.PinnedTag == null ? featureName : null,
                            detach = row.PinnedTag != null,
                            baseCommitSha = row.BaseCommitSha,
                            divergenceBaseBranch = row.ParentBranchName,
                            workspaceId = info.WorkspaceId,
                            repositoryId = link.RepositoryId,
                            capabilities
                        },
                        cancellationToken);

                    await using var writeDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                    var tracked = await writeDb.WorkspaceFeatureRepositories
                        .FirstAsync(r => r.WorkspaceFeatureRepositoryId == row.WorkspaceFeatureRepositoryId, cancellationToken);

                    if (!response.Success)
                    {
                        tracked.State = WorkspaceFeatureRepositoryState.NeedsRepair;
                        tracked.LastError = response.Error ?? "CreateGitWorktree failed.";
                        logger.FeatureRepositoryFailed("Repair", info.WorkspaceId, featureName, repoName, tracked.LastError);
                        await writeDb.SaveChangesAsync(cancellationToken);
                        results.Add((row.WorkspaceRepositoryId, new FeatureRepairRepositoryResult(
                            repoName, FeatureRepositoryOperationOutcome.Failed, tracked.LastError)));
                        return;
                    }

                    var payload = WorkerResponseJson.DeserializeWorkerResponse<CreateGitWorktreeWorkerResponse>(response.Data);
                    if (payload is null || !payload.Success)
                    {
                        tracked.State = WorkspaceFeatureRepositoryState.NeedsRepair;
                        tracked.LastError = payload?.ErrorMessage ?? "CreateGitWorktree returned no payload.";
                        logger.FeatureRepositoryFailed("Repair", info.WorkspaceId, featureName, repoName, tracked.LastError);
                        await writeDb.SaveChangesAsync(cancellationToken);
                        results.Add((row.WorkspaceRepositoryId, new FeatureRepairRepositoryResult(
                            repoName, FeatureRepositoryOperationOutcome.Failed, tracked.LastError)));
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(payload.WorktreePath))
                        tracked.WorktreePath = payload.WorktreePath;
                    tracked.State = WorkspaceFeatureRepositoryState.Ready;
                    tracked.LastError = null;
                    await writeDb.SaveChangesAsync(cancellationToken);
                    results.Add((row.WorkspaceRepositoryId, new FeatureRepairRepositoryResult(
                        repoName, FeatureRepositoryOperationOutcome.Succeeded, null)));
                    logger.FeatureRepositoryDone("Repair", info.WorkspaceId, featureName, repoName, "Ready");
                }
                finally
                {
                    var n = Interlocked.Increment(ref done);
                    progress?.Report(new OperationProgress($"Repaired {n} of {retryRows.Count} repositories", n, retryRows.Count));
                    gate.Release();
                }
            };

            // D10: a Pending/NeedsRepair Workspace-role root row is always retried alone, before any
            // Source row; if it still fails, the Source rows are left untouched and the Feature stays NeedsRepair.
            var rootRetryRow = retryRows.SingleOrDefault(r =>
                linkByWrId[r.WorkspaceRepositoryId].Role == WorkspaceRepositoryRole.Workspace);
            var sourceRetryRows = retryRows.Where(r => r != rootRetryRow).ToList();
            if (rootRetryRow != null)
                await retryRowAsync(rootRetryRow);
            if (rootRetryRow == null || !results.Any(r =>
                    r.WrId == rootRetryRow.WorkspaceRepositoryId && r.Result.Outcome == FeatureRepositoryOperationOutcome.Failed))
            {
                await Task.WhenAll(sourceRetryRows.Select(retryRowAsync));
            }
        }

        var ordered = results.OrderBy(r => r.WrId).Select(r => r.Result).ToList();
        var anyFailed = ordered.Any(r => r.Outcome == FeatureRepositoryOperationOutcome.Failed);

        await using var finalizeDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var feature = await finalizeDb.WorkspaceFeatures
            .FirstAsync(f => f.WorkspaceFeatureId == info.WorkspaceFeatureId, cancellationToken);

        if (anyFailed)
        {
            var firstError = ordered.First(r => r.Outcome == FeatureRepositoryOperationOutcome.Failed).Message
                ?? "One or more worktrees failed to create.";
            feature.LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair;
            feature.LastError = firstError;
            feature.UpdatedAt = DateTime.UtcNow;
            await finalizeDb.SaveChangesAsync(cancellationToken);
            return new RepairFeatureResult(false, firstError, ordered);
        }

        progress?.Report(new OperationProgress("Seeding Feature projections..."));
        await SeedInitialFeatureProjectionsAsync(finalizeDb, info.WorkspaceId, featureContextId, featureName, cancellationToken);
        feature.LifecycleState = WorkspaceFeatureLifecycleState.Ready;
        feature.LastError = null;
        feature.UpdatedAt = DateTime.UtcNow;
        await finalizeDb.SaveChangesAsync(cancellationToken);
        await InitializeCodeGraphAsync(info.WorkspaceId, featureContextId, progress, cancellationToken);
        return new RepairFeatureResult(true, null, ordered);
    }

    private async Task<RollbackFeatureResult?> CollectDirtyReposForRollbackAsync(
        WorkspaceFeatureContextId featureContextId,
        WorkspaceFeatureContextInfo info,
        CancellationToken cancellationToken)
    {
        var specialContextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(info.WorkspaceId, cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.WorkspaceFeatureRepositories.AsNoTracking()
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
            .ToListAsync(cancellationToken);
        var wrIds = rows.Select(r => r.WorkspaceRepositoryId).ToList();
        var links = await db.WorkspaceRepositories.AsNoTracking()
            .Include(l => l.Repository)
            .Where(l => wrIds.Contains(l.WorkspaceRepositoryId))
            .ToDictionaryAsync(l => l.WorkspaceRepositoryId, cancellationToken);

        // One InspectWorktree Worker round trip per repository - run them concurrently (same
        // SemaphoreSlim(MaxParallel) pattern as RepairFeatureCoreAsync/RollbackFeatureCoreAsync) instead
        // of one at a time, so this pre-check does not itself become the slow part of Roll back for a
        // Feature with many repositories. Ordered by WrId afterwards (ConcurrentBag has no ordering of
        // its own) so the result list is deterministic regardless of which repository's Worker call
        // happens to finish first.
        var dirty = new ConcurrentBag<(int WrId, FeatureRepairRepositoryResult Result)>();
        using (var gate = new SemaphoreSlim(MaxParallel))
        {
            var tasks = rows.Select(async row =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var repoName = links.TryGetValue(row.WorkspaceRepositoryId, out var link)
                        ? link.Repository?.RepositoryName ?? ""
                        : "";
                    var mainPath = await pathResolver.GetRepositoryPathAsync(specialContextId, row.WorkspaceRepositoryId, cancellationToken);
                    var disk = await InspectWorktreeDiskStatusAsync(mainPath, row.WorktreePath, null, null, cancellationToken);
                    if (!disk.StatusUnknown && disk.IsDirty == true)
                    {
                        dirty.Add((row.WorkspaceRepositoryId, new FeatureRepairRepositoryResult(
                            repoName, FeatureRepositoryOperationOutcome.Failed, "Has uncommitted changes.")));
                    }
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks);
        }

        if (dirty.IsEmpty)
            return null;

        return new RollbackFeatureResult(
            false,
            "One or more repositories have uncommitted changes. Commit or discard them before roll back.",
            dirty.OrderBy(d => d.WrId).Select(d => d.Result).ToList());
    }

    private async Task<RollbackFeatureResult> RollbackFeatureCoreAsync(
        WorkspaceFeatureContextId featureContextId,
        WorkspaceFeatureContextInfo info,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var featureName = info.FeatureName ?? "";
        var specialContextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(info.WorkspaceId, cancellationToken);
        using var monitoringPause = gitChangesMonitoringPause.Pause(featureContextId.Value);
        await UninitializeCodeGraphAsync(info.WorkspaceId, featureContextId, progress, cancellationToken);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.WorkspaceFeatureRepositories
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
            .ToListAsync(cancellationToken);
        var wrIds = rows.Select(r => r.WorkspaceRepositoryId).ToList();
        var links = await db.WorkspaceRepositories.AsNoTracking()
            .Include(l => l.Repository)
            .Where(l => wrIds.Contains(l.WorkspaceRepositoryId))
            .ToListAsync(cancellationToken);
        var linkByWrId = links.ToDictionary(l => l.WorkspaceRepositoryId);

        string? workspaceRoot = null;
        string? workspaceFolderName = null;
        string? workspaceRepositoryName = null;
        try
        {
            (workspaceRoot, workspaceFolderName, workspaceRepositoryName) =
                await pathResolver.GetWorkerArgsAsync(specialContextId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve Workspace worker paths for Feature roll back.");
        }

        string? featureRootPath = null;
        string? featureStorageRoot = null;
        try
        {
            featureRootPath = await pathResolver.GetContextRootAsync(featureContextId, cancellationToken);
            (featureStorageRoot, _, _) = await pathResolver.GetWorkerArgsAsync(featureContextId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve Feature storage paths for roll back.");
        }

        var results = new List<FeatureRepairRepositoryResult>();
        var done = 0;
        // D10: the Workspace-role root worktree is the Feature root and holds the Source worktrees, so
        // it is rolled back last and only when every Source row rolled back without a failure.
        var rootRow = rows.SingleOrDefault(r =>
            linkByWrId.TryGetValue(r.WorkspaceRepositoryId, out var rootLink)
            && rootLink.Role == WorkspaceRepositoryRole.Workspace);
        var orderedRows = rows.Where(r => r != rootRow).ToList();
        if (rootRow != null)
            orderedRows.Add(rootRow);
        const string rootKeptMessage = "Workspace repository worktree kept until all repository worktrees are removed.";
        var rootKept = false;
        foreach (var row in orderedRows)
        {
            var repoName = linkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var link)
                ? link.Repository?.RepositoryName ?? ""
                : "";
            var isRootRow = ReferenceEquals(row, rootRow);
            if (isRootRow && results.Any(r => r.Outcome == FeatureRepositoryOperationOutcome.Failed))
            {
                rootKept = true;
                results.Add(new FeatureRepairRepositoryResult(repoName, FeatureRepositoryOperationOutcome.Skipped, rootKeptMessage));
                done++;
                progress?.Report(new OperationProgress($"Rolled back {done} of {rows.Count} repositories", done, rows.Count));
                continue;
            }

            var mainPath = await pathResolver.GetRepositoryPathAsync(specialContextId, row.WorkspaceRepositoryId, cancellationToken);

            var listResp = await workerBridge.SendCommandAsync(
                WorkerHubMethods.ListGitWorktrees,
                new { mainRepositoryPath = mainPath },
                cancellationToken);
            GitWorktreeInfo? registered = null;
            string? primaryBranch = null;
            if (listResp.Success && listResp.Data != null)
            {
                var payload = WorkerResponseJson.DeserializeWorkerResponse<ListWorktreesWorkerResponse>(listResp.Data);
                var worktrees = payload?.Worktrees ?? [];
                registered = worktrees.FirstOrDefault(wt =>
                    WorkspaceFeatureReconciler.PathsEqualNormalized(wt.WorktreePath, row.WorktreePath));
                primaryBranch = worktrees
                    .FirstOrDefault(wt => WorkspaceFeatureReconciler.PathsEqualNormalized(wt.WorktreePath, mainPath))
                    ?.BranchName;
            }

            if (registered is null)
            {
                results.Add(new FeatureRepairRepositoryResult(repoName, FeatureRepositoryOperationOutcome.Skipped, "Worktree not registered."));
            }
            else
            {
                var removeResp = await workerBridge.SendCommandAsync(
                    WorkerHubMethods.RemoveGitWorktree,
                    new
                    {
                        mainRepositoryPath = mainPath,
                        worktreePath = row.WorktreePath,
                        force = false,
                        featureRootPath = isRootRow ? null : featureRootPath,
                        featureStorageRoot = isRootRow ? null : featureStorageRoot,
                        unlock = false
                    },
                    cancellationToken);
                if (!removeResp.Success)
                {
                    logger.FeatureRepositoryFailed("Rollback", info.WorkspaceId, featureName, repoName, removeResp.Error ?? "RemoveGitWorktree failed.");
                    results.Add(new FeatureRepairRepositoryResult(
                        repoName, FeatureRepositoryOperationOutcome.Failed, removeResp.Error ?? "RemoveGitWorktree failed."));
                    done++;
                    progress?.Report(new OperationProgress($"Rolled back {done} of {rows.Count} repositories", done, rows.Count));
                    continue;
                }

                var branchName = row.PinnedTag == null ? featureName : null;
                var defaultBranch = linkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var branchLink)
                    ? branchLink.DefaultBranchName
                    : null;
                var skipBranch =
                    string.IsNullOrWhiteSpace(branchName)
                    || (!string.IsNullOrWhiteSpace(defaultBranch)
                        && branchName.Equals(defaultBranch.Trim(), StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(primaryBranch)
                        && branchName.Equals(primaryBranch.Trim(), StringComparison.OrdinalIgnoreCase));

                if (!skipBranch
                    && !string.IsNullOrWhiteSpace(workspaceRoot)
                    && !string.IsNullOrWhiteSpace(workspaceFolderName)
                    && !string.IsNullOrWhiteSpace(repoName))
                {
                    var deleteLocal = await workerBridge.SendCommandAsync(
                        "DeleteBranch",
                        new
                        {
                            workspaceName = workspaceFolderName,
                            repositoryName = repoName,
                            workspaceRepositoryName,
                            branchName,
                            isRemote = false,
                            force = false,
                            workspaceRoot
                        },
                        cancellationToken);
                    if (!deleteLocal.Success)
                    {
                        logger.FeatureRepositoryFailed("Rollback", info.WorkspaceId, featureName, repoName, deleteLocal.Error ?? "Local branch kept (could not delete).");
                        results.Add(new FeatureRepairRepositoryResult(
                            repoName,
                            FeatureRepositoryOperationOutcome.Succeeded,
                            deleteLocal.Error ?? "Local branch kept (could not delete)."));
                        done++;
                        progress?.Report(new OperationProgress($"Rolled back {done} of {rows.Count} repositories", done, rows.Count));
                        continue;
                    }
                }

                results.Add(skipBranch && !string.IsNullOrWhiteSpace(branchName)
                    ? new FeatureRepairRepositoryResult(repoName, FeatureRepositoryOperationOutcome.Skipped, "Branch delete refused (default or primary checkout).")
                    : new FeatureRepairRepositoryResult(repoName, FeatureRepositoryOperationOutcome.Succeeded, null));
            }

            logger.FeatureRepositoryDone("Rollback", info.WorkspaceId, featureName, repoName, results[^1].Outcome.ToString());
            done++;
            progress?.Report(new OperationProgress($"Rolled back {done} of {rows.Count} repositories", done, rows.Count));
        }

        if (results.Any(r => r.Outcome == FeatureRepositoryOperationOutcome.Failed))
        {
            var first = results.First(r => r.Outcome == FeatureRepositoryOperationOutcome.Failed);
            return new RollbackFeatureResult(
                false,
                rootKept ? $"{first.Message} {rootKeptMessage}" : first.Message,
                results);
        }

        var special = specialContextId;
        var selected = await selectedContextService.GetSelectedAsync(info.WorkspaceId, cancellationToken);
        if (selected?.Value == featureContextId.Value)
            await selectedContextService.SetSelectedAsync(info.WorkspaceId, special, cancellationToken);

        await db.WorkspaceFeatureRepositories
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
            .ExecuteDeleteAsync(cancellationToken);
        foreach (var row in rows)
            db.Entry(row).State = EntityState.Detached;

        await DeleteContextScopedProjectDataAsync(db, featureContextId.Value, cancellationToken);
        var contextRow = await db.WorkspaceFeatureContexts
            .FirstAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value, cancellationToken);
        var featureRow = await db.WorkspaceFeatures
            .FirstAsync(f => f.WorkspaceFeatureId == info.WorkspaceFeatureId, cancellationToken);
        db.WorkspaceFeatureContexts.Remove(contextRow);
        db.WorkspaceFeatures.Remove(featureRow);
        await db.SaveChangesAsync(cancellationToken);

        return new RollbackFeatureResult(true, null, results);
    }

    private sealed class ProjectKeyComparer : IEqualityComparer<(int RepositoryId, string Name)>
    {
        public bool Equals((int RepositoryId, string Name) x, (int RepositoryId, string Name) y) =>
            x.RepositoryId == y.RepositoryId && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((int RepositoryId, string Name) key) =>
            HashCode.Combine(key.RepositoryId, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }

    private sealed class ListWorktreesWorkerResponse
    {
        [JsonPropertyName("worktrees")]
        public List<GitWorktreeInfo>? Worktrees { get; set; }
    }

    internal async Task SeedInitialFeatureProjectionsAsync(
        AppDbContext db,
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string featureBranch,
        CancellationToken cancellationToken)
    {
        var specialId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        var specialStates = await db.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == specialId.Value)
            .ToDictionaryAsync(s => s.WorkspaceRepositoryId, cancellationToken);

        var featureRowsByLinkId = await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Where(r => r.WorkspaceFeatureContextId == contextId.Value)
            .ToDictionaryAsync(r => r.WorkspaceRepositoryId, r => new { r.BaseCommitSha, r.PinnedTag }, cancellationToken);

        // Worktree create can trigger SyncCommand attribution into this Feature context before seed runs,
        // so upsert against any rows Sync already wrote (UNIQUE on context+repo / context+repo+name).
        var existingStates = await db.WorkspaceRepositoryContextStates
            .Where(s => s.WorkspaceFeatureContextId == contextId.Value)
            .ToDictionaryAsync(s => s.WorkspaceRepositoryId, cancellationToken);

        // Workspace grid SyncStatus / GitVersion come from the link for the special context
        // (Project() reads wr.SyncStatus). Feature grids read context state instead, so seed from
        // the link as the source of truth — special-state SyncStatus can lag and left new Features all-red.
        foreach (var link in links)
        {
            specialStates.TryGetValue(link.WorkspaceRepositoryId, out var src);
            featureRowsByLinkId.TryGetValue(link.WorkspaceRepositoryId, out var featureRow);
            var baseSha = featureRow?.BaseCommitSha;
            // Tag-pinned repositories mirror the Workspace tag identity (detached, no branch, no counts).
            var pinnedTag = featureRow?.PinnedTag;
            var onTag = pinnedTag != null;
            if (existingStates.TryGetValue(link.WorkspaceRepositoryId, out var existing))
            {
                // The row already exists: a Sync or an earlier (interrupted) seed wrote it. Only structure is
                // reconciled here. GitVersion, SyncStatus and the commit counts belong to whoever probed them -
                // the Workspace's values describe another branch, and a retry must not undo a user's commits.
                existing.BranchName = onTag ? null : featureBranch;
                existing.CheckedOutTag = pinnedTag;
                existing.HeadCommit ??= baseSha ?? src?.HeadCommit;
                existing.HasNewerTag = onTag ? src?.HasNewerTag ?? link.HasNewerTag : null;
                existing.Projects = src?.Projects ?? link.Projects;
                existing.RepositoryType = src?.RepositoryType ?? link.RepositoryType;
                if (!onTag)
                {
                    existing.OutgoingCommits ??= 0;
                    existing.IncomingCommits ??= src?.IncomingCommits ?? link.IncomingCommits;
                    existing.DefaultBranchBehindCommits ??= 0;
                    existing.DefaultBranchAheadCommits ??= 0;
                    existing.BranchHasUpstream ??= false;
                }
                // A row that never got a probe result (null flag) still has no Feature version: mark it pending.
                if (existing.GitVersionPending is null && existing.GitVersion is null)
                    existing.GitVersionPending = true;
                existing.DependencyLevel = src?.DependencyLevel ?? link.DependencyLevel;
                existing.Dependencies = src?.Dependencies ?? link.Dependencies;
                existing.UnmatchedDeps = src?.UnmatchedDeps ?? link.UnmatchedDeps;
                existing.OutOfDateFileLines = src?.OutOfDateFileLines ?? link.OutOfDateFileLines;
                existing.OutOfDateFileRepos = src?.OutOfDateFileRepos ?? link.OutOfDateFileRepos;
                existing.TotalFileConfigRepos = src?.TotalFileConfigRepos ?? link.TotalFileConfigRepos;
                existing.HasSelfFileVersionToken = src?.HasSelfFileVersionToken ?? link.HasSelfFileVersionToken;
                existing.TotalFileLines = src?.TotalFileLines ?? link.TotalFileLines;
                continue;
            }

            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                BranchName = onTag ? null : featureBranch,
                CheckedOutTag = pinnedTag,
                // Feature HEAD starts at the creation tip - Create PR uses HeadCommit != BaseCommitSha
                // (ahead of Feature parent), not DefaultBranchAhead vs main.
                HeadCommit = baseSha ?? src?.HeadCommit,
                HasNewerTag = onTag ? src?.HasNewerTag ?? link.HasNewerTag : null,
                // The Workspace's version describes its own branch, not this Feature's: leave it unset and pending
                // until the deferred fresh-worktree GitVersion arrives, so no comparison is ever shown as validated.
                GitVersion = null,
                GitVersionPending = true,
                Projects = src?.Projects ?? link.Projects,
                RepositoryType = src?.RepositoryType ?? link.RepositoryType,
                OutgoingCommits = onTag ? null : 0,
                IncomingCommits = onTag ? null : src?.IncomingCommits ?? link.IncomingCommits,
                // Feature divergence is vs ParentBranchName (PR base), not vs main. At create,
                // HEAD == BaseCommitSha so the Feature is neither ahead nor behind its parent tip.
                DefaultBranchBehindCommits = onTag ? null : 0,
                DefaultBranchAheadCommits = onTag ? null : 0,
                BranchHasUpstream = onTag ? null : false,
                SyncStatus = link.SyncStatus,
                DependencyLevel = src?.DependencyLevel ?? link.DependencyLevel,
                Dependencies = src?.Dependencies ?? link.Dependencies,
                UnmatchedDeps = src?.UnmatchedDeps ?? link.UnmatchedDeps,
                OutOfDateFileLines = src?.OutOfDateFileLines ?? link.OutOfDateFileLines,
                OutOfDateFileRepos = src?.OutOfDateFileRepos ?? link.OutOfDateFileRepos,
                TotalFileConfigRepos = src?.TotalFileConfigRepos ?? link.TotalFileConfigRepos,
                HasSelfFileVersionToken = src?.HasSelfFileVersionToken ?? link.HasSelfFileVersionToken,
                TotalFileLines = src?.TotalFileLines ?? link.TotalFileLines
            });
        }

        var specialProjects = await db.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && p.WorkspaceFeatureContextId == specialId.Value)
            .ToListAsync(cancellationToken);

        // Generated/virtual packages are workspace-global (special context only). Cloning them into
        // Feature contexts duplicates (RepositoryId, PackageId) and breaks SyncGeneratedPackageDependenciesAsync.
        var generatedSpecialIds = specialProjects.Where(p => p.IsGenerated).Select(p => p.ProjectId).ToHashSet();
        var realSpecialProjects = specialProjects.Where(p => !p.IsGenerated).ToList();

        // Every other writer of this context's projects and edges (Sync merges) takes the same gate, so the
        // lookup-then-insert below cannot race them on the unique (context, repository, name) index.
        using var projectionGate = finalizationCoordinator is null
            ? null
            : await finalizationCoordinator.AcquireProjectionAsync(contextId.Value, cancellationToken);

        // Drop any Feature-scoped generated copies Sync/legacy seed may have written. Saved on its own, before any
        // insert, because the unique index ignores IsGenerated: a stale generated row must be gone first.
        var featureScopedGenerated = await db.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId
                        && p.WorkspaceFeatureContextId == contextId.Value
                        && p.IsGenerated)
            .ToListAsync(cancellationToken);
        if (featureScopedGenerated.Count > 0)
        {
            var orphanIds = featureScopedGenerated.Select(p => p.ProjectId).ToHashSet();
            var orphanEdges = await db.ProjectDependencies
                .Where(d => orphanIds.Contains(d.DependentProjectId) || orphanIds.Contains(d.ReferencedProjectId))
                .ToListAsync(cancellationToken);
            if (orphanEdges.Count > 0)
                db.ProjectDependencies.RemoveRange(orphanEdges);
            db.WorkspaceProjects.RemoveRange(featureScopedGenerated);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Same identity as WorkspaceProjectRepository's merge: trimmed name, ordinal-ignore-case. The database index
        // is stricter (exact name), so two rows this key treats as one can never violate it.
        var existingFeatureProjects = await db.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId
                        && p.WorkspaceFeatureContextId == contextId.Value
                        && !p.IsGenerated)
            .OrderBy(p => p.ProjectId)
            .ToListAsync(cancellationToken);
        var projectKeyComparer = new ProjectKeyComparer();
        var featureProjectsByKey = new Dictionary<(int RepositoryId, string Name), WorkspaceProject>(projectKeyComparer);
        foreach (var p in existingFeatureProjects)
            featureProjectsByKey.TryAdd((p.RepositoryId, p.ProjectName.Trim()), p);

        // projectIdMap: special real ProjectId -> Feature-context project (cloned or Sync-preexisting).
        // Sources are visited in a fixed order, so when two sources share one identity the first always wins and the
        // others map onto the same Feature project (their edges collapse into its edges).
        var projectIdMap = new Dictionary<int, WorkspaceProject>();
        var inserted = 0;
        var updated = 0;
        var collisions = 0;
        foreach (var src in realSpecialProjects
                     .OrderBy(p => p.RepositoryId)
                     .ThenBy(p => p.ProjectName.Trim(), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(p => p.ProjectName, StringComparer.Ordinal)
                     .ThenBy(p => p.ProjectId))
        {
            var name = src.ProjectName.Trim();
            var key = (src.RepositoryId, name);
            if (featureProjectsByKey.TryGetValue(key, out var existing))
            {
                var isNewThisRun = db.Entry(existing).State == EntityState.Added;
                if (isNewThisRun)
                {
                    collisions++;
                    logger.LogWarning(
                        "Feature {ContextId}: source projects '{First}' and '{Second}' in repository {RepositoryId} share one project identity; both map to one Feature project.",
                        contextId.Value, existing.ProjectName, src.ProjectName, src.RepositoryId);
                }
                else
                {
                    updated++;
                }

                existing.ProjectType = src.ProjectType;
                existing.ProjectFilePath = src.ProjectFilePath;
                existing.TargetFramework = src.TargetFramework;
                existing.PackageId = src.PackageId;
                projectIdMap[src.ProjectId] = existing;
                continue;
            }

            var clone = new WorkspaceProject
            {
                WorkspaceId = workspaceId,
                WorkspaceFeatureContextId = contextId.Value,
                RepositoryId = src.RepositoryId,
                ProjectName = name,
                ProjectType = src.ProjectType,
                ProjectFilePath = src.ProjectFilePath,
                TargetFramework = src.TargetFramework,
                PackageId = src.PackageId,
                IsGenerated = false
            };
            db.WorkspaceProjects.Add(clone);
            featureProjectsByKey[key] = clone;
            projectIdMap[src.ProjectId] = clone;
            inserted++;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Feature {ContextId}: projection seed projects Workspace={WorkspaceId} Inserted={Inserted} Updated={Updated} Collisions={Collisions} Source=Seed",
            contextId.Value, workspaceId, inserted, updated, collisions);

        if (projectIdMap.Count > 0)
        {
            var srcIds = projectIdMap.Keys.Concat(generatedSpecialIds).ToList();
            var deps = await db.ProjectDependencies
                .AsNoTracking()
                .Where(d => srcIds.Contains(d.DependentProjectId) || srcIds.Contains(d.ReferencedProjectId))
                .ToListAsync(cancellationToken);

            var featureProjectIds = projectIdMap.Values.Select(p => p.ProjectId).ToList();
            var existingEdgeKeys = await db.ProjectDependencies
                .AsNoTracking()
                .Where(d => featureProjectIds.Contains(d.DependentProjectId))
                .Select(d => new { d.DependentProjectId, d.ReferencedProjectId })
                .ToListAsync(cancellationToken);
            var existingEdgeSet = existingEdgeKeys
                .Select(e => (e.DependentProjectId, e.ReferencedProjectId))
                .ToHashSet();

            foreach (var dep in deps)
            {
                if (!projectIdMap.TryGetValue(dep.DependentProjectId, out var depClone))
                    continue;

                int referencedId;
                if (projectIdMap.TryGetValue(dep.ReferencedProjectId, out var refClone))
                    referencedId = refClone.ProjectId;
                else if (generatedSpecialIds.Contains(dep.ReferencedProjectId))
                    referencedId = dep.ReferencedProjectId; // shared workspace-global generated row
                else
                    continue;

                var edgeKey = (depClone.ProjectId, referencedId);
                if (!existingEdgeSet.Add(edgeKey))
                    continue;

                db.ProjectDependencies.Add(new ProjectDependency
                {
                    DependentProjectId = depClone.ProjectId,
                    ReferencedProjectId = referencedId,
                    Version = dep.Version
                });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        // Recompute levels from the cloned Feature graph so headers match projects/deps even when
        // special-Workspace context state was missing DependencyLevel.
        try
        {
            await using var statsScope = scopeFactory.CreateAsyncScope();
            var recomputeScope = statsScope.ServiceProvider.GetRequiredService<WorkspaceStateRecomputeScope>();
            await recomputeScope.RecomputeDependencyStatsAsync(workspaceId, contextId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Feature {ContextId}: dependency-level recompute after seed failed; copied levels may still apply.",
                contextId.Value);
        }

        var featureContext = await db.WorkspaceFeatureContexts
            .FirstAsync(c => c.WorkspaceFeatureContextId == contextId.Value, cancellationToken);
        featureContext.IsInSync = links.All(l => l.SyncStatus == RepoSyncStatus.InSync);
        if (featureContext.IsInSync)
            featureContext.LastSyncedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, string?>> GetParentBranchNamesByRepositoryIdAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default)
    {
        var info = await contextResolver.GetRequiredAsync(featureContextId, cancellationToken: cancellationToken);
        if (info.IsSpecialWorkspace)
            return new Dictionary<int, string?>();

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
            .Join(
                db.WorkspaceRepositories.AsNoTracking(),
                r => r.WorkspaceRepositoryId,
                l => l.WorkspaceRepositoryId,
                (r, l) => new { l.RepositoryId, r.ParentBranchName })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(x => x.RepositoryId, x => x.ParentBranchName);
    }

    private async Task<(Dictionary<string, string> Commits, Dictionary<string, string> Branches, Dictionary<string, string> Tags, Dictionary<string, List<string>> BranchCollisions)> GetHeadSnapshotAsync(
        Workspace workspace,
        IReadOnlyList<string> repositoryNames,
        string? workspaceRepositoryName,
        string collisionBranchName,
        CancellationToken cancellationToken)
    {
        var emptyCommits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var emptyBranches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var emptyTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var emptyCollisions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var root = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var response = await workerBridge.SendCommandAsync(
            WorkerHubMethods.GetHeadCommits,
            new
            {
                workspaceId = workspace.WorkspaceId,
                workspaceName = workspace.Name,
                workspaceRoot = root,
                repositoryNames,
                workspaceRepositoryName,
                collisionBranchName
            },
            cancellationToken);

        if (!response.Success)
            return (emptyCommits, emptyBranches, emptyTags, emptyCollisions);

        var payload = WorkerResponseJson.DeserializeWorkerResponse<GetHeadCommitsWorkerResponse>(response.Data);
        return (
            payload?.Commits ?? emptyCommits,
            payload?.Branches ?? emptyBranches,
            payload?.Tags ?? emptyTags,
            payload?.BranchCollisions ?? emptyCollisions);
    }

    private async Task EnsureManagedFeatureStorageRootAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        // Keep any already-persisted root so reconfiguring Settings does not orphan existing Features.
        // Relocate only the legacy drive-root bug (C:\.graymoon\...).
        if (!string.IsNullOrWhiteSpace(workspace.ManagedFeatureStorageRoot)
            && !WorkerPath.IsLegacyWindowsDriveRootGraymoonPath(workspace.ManagedFeatureStorageRoot))
            return;

        var storageRoot = await workspaceService.ResolveFeatureStorageRootPathAsync(
            persistIfMissing: true,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new InvalidOperationException(
                "Feature storage root is not configured. Set it on the Settings page (or connect the Worker so the host user profile can be used as the default).");

        // Keep the Worker-facing Feature storage root in whichever shape the Worker already
        // uses (Windows or POSIX) - never the App's own OS (it may run in a Linux Docker container).
        workspace.ManagedFeatureStorageRoot = WorkerPath.Combine(storageRoot, workspace.Name, "features");
    }

    private static RemoveFeatureClassification Classify(IReadOnlyList<RemoveFeatureRepositoryPlan> plans)
    {
        // A previous delete error (folder busy, permission denied) stays on the repository line as
        // Warning. It does not override pull-request classification: the retry is the same removal
        // once the folder is free. A missing worktree is different — GrayMoon cannot confirm the folder.
        if (plans.Any(p => !p.WorktreeExists))
            return RemoveFeatureClassification.NeedsRepair;
        // Merged PRs and never-created PRs (fresh Feature with no commits/PR) are Completed-equivalent
        // so Remove is automatically safe when the live worktree is clean.
        if (plans.All(IsPrMergedOrNeverCreated))
            return RemoveFeatureClassification.Completed;
        if (plans.Any(p => string.Equals(p.PullRequestState, "closed", StringComparison.OrdinalIgnoreCase)
                           && p.PullRequestMerged != true))
            return RemoveFeatureClassification.Abandoned;
        return RemoveFeatureClassification.Active;
    }

    /// <summary>
    /// True when the repo's PR is merged, or it has no commits beyond the default branch and no PR was
    /// ever opened (null/empty number and state). A repo with live commits ahead of default but no PR
    /// yet is not in this bucket - it still has work pending a pull request. EffectiveAheadOfDefault
    /// reads the Feature branch's own count for a non-pinned repo (09 SB-2), never the checked-out
    /// branch; a null count (unknown) is never treated as zero.
    /// </summary>
    private static bool IsPrMergedOrNeverCreated(RemoveFeatureRepositoryPlan p) =>
        p.PullRequestMerged == true
        || (p.EffectiveAheadOfDefault == 0 && p.PullRequestNumber is null or 0 && string.IsNullOrWhiteSpace(p.PullRequestState));

    /// <summary>
    /// Forwards progress to the structural overlay operation (so BackgroundJobOverlay updates)
    /// and to any caller-supplied progress sink.
    /// </summary>
    private static IProgress<OperationProgress> BindOverlayProgress(
        IWorkspaceLockedOperation op,
        IProgress<OperationProgress>? progress)
        => new Progress<OperationProgress>(p =>
        {
            op.ReportProgress(p.Message);
            progress?.Report(p);
        });

    private static CreateFeatureResult FailCreate(string condition, string error) => new()
    {
        Success = false,
        Condition = condition,
        Error = error
    };

    private sealed class GetHeadCommitsWorkerResponse
    {
        [JsonPropertyName("commits")]
        public Dictionary<string, string>? Commits { get; set; }

        [JsonPropertyName("branches")]
        public Dictionary<string, string>? Branches { get; set; }

        [JsonPropertyName("tags")]
        public Dictionary<string, string>? Tags { get; set; }

        [JsonPropertyName("branchCollisions")]
        public Dictionary<string, List<string>>? BranchCollisions { get; set; }
    }

    private sealed class CreateGitWorktreeWorkerResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("worktreePath")]
        public string? WorktreePath { get; set; }
    }
}
