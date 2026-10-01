using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Agent;
using GrayMoon.App.Services.Git;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application;
using GrayMoon.Application.Features;
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
    IAgentBridge agentBridge,
    WorkspaceService workspaceService,
    WorkspacePullRequestService workspacePullRequestService,
    IWorkspaceGitChangesMonitoringPause gitChangesMonitoringPause,
    IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<WorkspaceFeatureOperations> logger) : IWorkspaceFeatureOperations
{
    private static readonly Regex BranchNamePattern = new(
        @"^(?!.*\.\.)(?!/)(?!.*/$)(?!.*//)(?!.*[@{])[^\s~^:?*\[\\]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private int MaxParallel => Math.Max(1, workspaceOptions.Value.MaxParallelOperations);

    public async Task<CreateFeatureResult> CreateFeatureAsync(
        int workspaceId,
        string featureName,
        WorkspaceFeatureBaseKindApplication baseKind,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var name = (featureName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name) || !BranchNamePattern.IsMatch(name))
            return FailCreate("InvalidFeatureName", "Feature name is not a valid Git branch name.");

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
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                    var overlayProgress = BindOverlayProgress(op, progress);
                    var result = await CreateFeatureCoreAsync(workspaceId, name, overlayProgress, linked.Token);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(FailCreate("Exception", ex.Message));
                    throw;
                }
            },
            out _);

        if (!started)
            return FailCreate("WorkspaceBusy", "A Workspace structural operation is already running.");

        return await tcs.Task.WaitAsync(cancellationToken);
    }

    private async Task<CreateFeatureResult> CreateFeatureCoreAsync(
        int workspaceId,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId && f.Name == name, cancellationToken))
            return FailCreate("DuplicateName", $"A Feature named '{name}' already exists.");

        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace {workspaceId} was not found.");

        await EnsureManagedFeatureStorageRootAsync(workspace, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

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

        var snapshot = await GetHeadSnapshotAsync(workspace, repoNames, name, cancellationToken);
        if (snapshot.Commits.Count != repoNames.Count)
            return FailCreate("HeadCommitsIncomplete", "Could not resolve HEAD for every Workspace repository.");

        if (snapshot.BranchCollisions.Count > 0)
        {
            var details = snapshot.BranchCollisions
                .OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                .Select(c => $"{c.Key} ({string.Join(", ", c.Value)})");
            return FailCreate(
                "BranchExists",
                $"Branch '{name}' already exists in {snapshot.BranchCollisions.Count} of {repoNames.Count} repositories: "
                + string.Join("; ", details)
                + ". Choose a different Feature name or delete those branches first.");
        }

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
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync(cancellationToken);

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = workspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = now,
            IsInSync = false
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync(cancellationToken);

        var contextId = new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
        var pendingRows = new List<WorkspaceFeatureRepository>();
        foreach (var link in links)
        {
            var repoName = link.Repository!.RepositoryName;
            if (!snapshot.Commits.TryGetValue(repoName, out var sha) || string.IsNullOrWhiteSpace(sha))
                return FailCreate("HeadCommitsIncomplete", $"Missing HEAD for repository '{repoName}'.");

            // Parent branch from the same agent snapshot as BaseCommitSha. Detached HEAD -> null
            // (do not invent a name from mutable Workspace link state).
            snapshot.Branches.TryGetValue(repoName, out var parentBranch);
            parentBranch = string.IsNullOrWhiteSpace(parentBranch) ? null : parentBranch.Trim();

            // Repositories on a tag stay on that tag in the Feature: detached worktree, no Feature branch.
            snapshot.Tags.TryGetValue(repoName, out var pinnedTag);
            pinnedTag = string.IsNullOrWhiteSpace(pinnedTag) ? null : pinnedTag.Trim();

            var worktreePath = await pathResolver.GetRepositoryPathAsync(contextId, link.WorkspaceRepositoryId, cancellationToken);
            var row = new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                WorktreePath = worktreePath,
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

        var anyFailure = 0;
        var createCompleted = 0;
        var createTotal = pendingRows.Count;
        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = pendingRows.Select(async row =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var link = links.First(l => l.WorkspaceRepositoryId == row.WorkspaceRepositoryId);
                var mainPath = await pathResolver.GetRepositoryPathAsync(
                    specialContextId, link.WorkspaceRepositoryId, cancellationToken);
                var response = await agentBridge.SendCommandAsync(
                    AgentHubMethods.CreateGitWorktree,
                    new
                    {
                        mainRepositoryPath = mainPath,
                        worktreePath = row.WorktreePath,
                        branchName = row.PinnedTag == null ? name : null,
                        detach = row.PinnedTag != null,
                        baseCommitSha = row.BaseCommitSha,
                        divergenceBaseBranch = row.ParentBranchName,
                        workspaceId,
                        repositoryId = link.RepositoryId
                    },
                    cancellationToken);

                await using var writeDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var tracked = await writeDb.WorkspaceFeatureRepositories
                    .FirstAsync(r => r.WorkspaceFeatureRepositoryId == row.WorkspaceFeatureRepositoryId, cancellationToken);

                if (!response.Success)
                {
                    tracked.State = WorkspaceFeatureRepositoryState.NeedsRepair;
                    tracked.LastError = response.Error ?? "CreateGitWorktree failed.";
                    Interlocked.Exchange(ref anyFailure, 1);
                }
                else
                {
                    var payload = AgentResponseJson.DeserializeAgentResponse<CreateGitWorktreeAgentResponse>(response.Data);
                    if (payload is null || !payload.Success)
                    {
                        tracked.State = WorkspaceFeatureRepositoryState.NeedsRepair;
                        tracked.LastError = payload?.ErrorMessage ?? "CreateGitWorktree returned no payload.";
                        Interlocked.Exchange(ref anyFailure, 1);
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(payload.WorktreePath))
                            tracked.WorktreePath = payload.WorktreePath;
                        tracked.State = WorkspaceFeatureRepositoryState.Ready;
                        tracked.LastError = null;
                    }
                }

                await writeDb.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                var done = Interlocked.Increment(ref createCompleted);
                progress.Report(
                    $"Created feature in {done} of {createTotal} repos",
                    done,
                    createTotal);
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

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
                return FailCreate("NeedsRepair", trackedFeature.LastError);
            }

            progress?.Report(new OperationProgress("Seeding Feature projections..."));
            await SeedInitialFeatureProjectionsAsync(finalizeDb, workspaceId, contextId, name, cancellationToken);

            trackedFeature.LifecycleState = WorkspaceFeatureLifecycleState.Ready;
            trackedFeature.LastError = null;
            trackedFeature.UpdatedAt = DateTime.UtcNow;
            await finalizeDb.SaveChangesAsync(cancellationToken);
        }

        await selectedContextService.SetSelectedAsync(workspaceId, contextId, cancellationToken);
        progress?.Report(new OperationProgress($"Feature '{name}' is ready."));
        return new CreateFeatureResult
        {
            Success = true,
            ContextId = contextId,
            WorkspaceFeatureId = feature.WorkspaceFeatureId
        };
    }

    public async Task<RemoveFeaturePlan> AnalyzeRemoveFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default)
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

        var pullRequestStatusUnknown = false;
        try
        {
            var outcomes = await workspacePullRequestService.RefreshContextPullRequestsAsync(
                info.WorkspaceId, featureContextId.Value, branchByRepositoryId, force: false, cancellationToken);
            pullRequestStatusUnknown = outcomes.Values.Any(o => o == PullRequestRefreshOutcome.Failed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pull request refresh failed for Feature remove analysis.");
            pullRequestStatusUnknown = true;
        }

        var prs = await db.WorkspaceRepositoryContextPullRequests
            .AsNoTracking()
            .Where(p => p.WorkspaceFeatureContextId == featureContextId.Value)
            .ToListAsync(cancellationToken);

        string? featureWorkspaceRoot = null;
        string? featureWorkspaceFolder = null;
        try
        {
            (featureWorkspaceRoot, featureWorkspaceFolder) =
                await pathResolver.GetAgentWorkspaceArgsAsync(featureContextId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve Feature workspace paths for remove analysis.");
        }

        var planSlots = new RemoveFeatureRepositoryPlan[rows.Count];
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
                        info.WorkspaceId,
                        row,
                        disk.Exists,
                        cancellationToken);

                    // The live current branch, from the Agent; null when detached or when disk status
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
                        FeatureBranchAheadOfUpstream = disk.StatusUnknown ? null : disk.FeatureBranchAheadOfUpstream
                    };
                }
                finally
                {
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

    public async Task<OperationResult> RemoveFeatureAsync(
        WorkspaceFeatureContextId featureContextId,
        RemoveFeatureOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = await contextResolver.GetRequiredAsync(featureContextId, cancellationToken: cancellationToken);
        if (info.IsSpecialWorkspace)
            return OperationResult.Fail("Cannot remove the special Workspace context.");

        var plan = await AnalyzeRemoveFeatureAsync(featureContextId, cancellationToken);
        if (!plan.Success)
            return OperationResult.Fail(plan.Error ?? "Analyze failed.");

        // Unknown disk state (Agent unreachable, or InspectWorktree failed) can hide real dirty work,
        // so Remove is refused here regardless of discard/force authorization - see A2 rule.
        if (plan.Repositories.Any(r => r.WorktreeStatusUnknown))
        {
            return OperationResult.Fail(
                "Could not check one or more repositories. Make sure the Worker is running, then try again.");
        }

        if (!plan.IsAutomaticallySafe
            && !options.AllowDiscardUncommitted
            && !options.AllowForceDeleteLocalBranches
            && !options.AllowUnlockWorktrees)
        {
            return OperationResult.Fail(
                "Feature removal is not automatically safe; authorize discard/force options explicitly.");
        }

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
                    var outcome = await RemoveFeatureCoreAsync(featureContextId, info, options, plan, overlayProgress, linked.Token);
                    tcs.TrySetResult(outcome);
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(OperationResult.Fail(ex.Message));
                    throw;
                }
            },
            out _);

        if (!started)
            return OperationResult.Fail("A Workspace structural operation is already running.");

        return await tcs.Task.WaitAsync(cancellationToken);
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
        // move to Removing. Skipping them keeps a retry from re-asking the Agent about an already gone worktree.
        var rows = await db.WorkspaceFeatureRepositories
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value
                && r.State != WorkspaceFeatureRepositoryState.Removed)
            .ToListAsync(cancellationToken);

        feature.LifecycleState = WorkspaceFeatureLifecycleState.Removing;
        feature.UpdatedAt = DateTime.UtcNow;
        foreach (var row in rows)
            row.State = WorkspaceFeatureRepositoryState.Removing;
        await db.SaveChangesAsync(cancellationToken);

        var wrIds = rows.Select(r => r.WorkspaceRepositoryId).ToList();
        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Include(l => l.Repository)
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
        try
        {
            (workspaceRoot, workspaceFolderName) =
                await pathResolver.GetAgentWorkspaceArgsAsync(specialContextId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve Workspace agent paths for Feature branch delete.");
        }

        // D1's Agent-side residue cleanup only deletes files when both of these are set; an old App
        // (or a resolution failure here) leaves them null, matching the old, report-only behaviour.
        string? featureRootPath = null;
        string? featureStorageRoot = null;
        try
        {
            featureRootPath = await pathResolver.GetContextRootAsync(featureContextId, cancellationToken);
            (featureStorageRoot, _) = await pathResolver.GetAgentWorkspaceArgsAsync(featureContextId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve Feature storage paths for worktree residue cleanup.");
        }

        using (var gate = new SemaphoreSlim(MaxParallel))
        {
            var removeTasks = rows.Select(async row =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var repoName = linkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var reportLink)
                        ? reportLink.Repository?.RepositoryName ?? ""
                        : "";

                    var mainPath = await pathResolver.GetRepositoryPathAsync(
                        specialContextId, row.WorkspaceRepositoryId, cancellationToken);
                    // Force worktree remove only when the user authorized discarding dirty Feature files.
                    // AllowForceDeleteLocalBranches does not imply discard permission.
                    var force = options.AllowDiscardUncommitted;
                    var response = await agentBridge.SendCommandAsync(
                        AgentHubMethods.RemoveGitWorktree,
                        new
                        {
                            mainRepositoryPath = mainPath,
                            worktreePath = row.WorktreePath,
                            force,
                            featureRootPath,
                            featureStorageRoot,
                            unlock = options.AllowUnlockWorktrees
                        },
                        cancellationToken);
                    if (!response.Success)
                    {
                        logger.LogWarning(
                            "RemoveGitWorktree failed for {Path}: {Error}",
                            row.WorktreePath, response.Error);
                        errorsByWrId[row.WorkspaceRepositoryId] = string.IsNullOrWhiteSpace(response.Error)
                            ? $"Failed to remove worktree {row.WorktreePath}."
                            : response.Error;
                        return;
                    }

                    var worktreeResult = AgentResponseJson.DeserializeAgentResponse<RemoveGitWorktreeResult>(response.Data);

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

                    // §27.8: after worktree remove, delete the Feature branch from the main repository.
                    // Tag-pinned repositories never got a Feature branch; a same-named branch there is not ours.
                    var branchName = row.PinnedTag == null ? info.FeatureName : null;
                    RemoveFeatureBranchOutcome branchOutcome;
                    string? branchMessage;
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
                    else
                    {
                        var deleteLocal = await agentBridge.SendCommandAsync(
                            "DeleteBranch",
                            new
                            {
                                workspaceName = workspaceFolderName,
                                repositoryName = repoName,
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
                                branchName, repoName, deleteLocal.Error);
                        }

                        if (options.DeleteRemoteBranches)
                        {
                            var deleteRemote = await agentBridge.SendCommandAsync(
                                "DeleteBranch",
                                new
                                {
                                    workspaceName = workspaceFolderName,
                                    repositoryName = repoName,
                                    branchName,
                                    isRemote = true,
                                    force = false,
                                    workspaceRoot
                                },
                                cancellationToken);
                            if (!deleteRemote.Success)
                            {
                                logger.LogWarning(
                                    "Delete remote Feature branch {Branch} failed for {Repo}: {Error}",
                                    branchName, repoName, deleteRemote.Error);
                            }
                        }
                    }

                    // 09 SB-2: when the worktree was not on its Feature branch, name the branch that was
                    // actually kept (or "(detached commit)" when there was none) so the report never
                    // only says "removed" while leaving an unmerged branch silently behind.
                    var keptBranchName = driftByWrId.TryGetValue(row.WorkspaceRepositoryId, out var driftPlan)
                        && driftPlan.IsOffFeatureBranch
                        ? driftPlan.CheckedOutBranch ?? "(detached commit)"
                        : null;

                    reportByWrId[row.WorkspaceRepositoryId] = new RemoveFeatureRepositoryReport(
                        row.WorkspaceRepositoryId,
                        repoName,
                        WorktreeRemoved: true,
                        branchOutcome,
                        branchMessage,
                        worktreeResult?.ResidueRemaining ?? false,
                        worktreeResult?.ResidueFileCount ?? 0,
                        worktreeResult?.ResidueSampleFiles,
                        worktreeResult?.ResidueMessage,
                        keptBranchName);
                }
                finally
                {
                    var done = Interlocked.Increment(ref removeCompleted);
                    progress.Report(
                        $"Removed feature from {done} of {removeTotal} repos",
                        done,
                        removeTotal);
                    gate.Release();
                }
            });
            await Task.WhenAll(removeTasks);
        }

        if (!errorsByWrId.IsEmpty)
        {
            // Agent work ran in parallel; apply EF updates sequentially (DbContext is not thread-safe).
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
            feature.LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair;
            feature.LastError = firstError;
            feature.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return OperationResult.Fail(firstError);
        }

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
        return OperationResult.Ok() with { RemoveFeatureReport = report };
    }

    /// <summary>
    /// App-side shape of the Agent's RemoveGitWorktree response (GrayMoon.Agent.Jobs.Response is not
    /// referenced here), used only to read the residue fields added for the Remove report (D1/D2).
    /// </summary>
    private sealed class RemoveGitWorktreeResult
    {
        public bool Success { get; set; }
        public bool ResidueRemaining { get; set; }
        public int ResidueFileCount { get; set; }
        public List<string>? ResidueSampleFiles { get; set; }
        public string? ResidueMessage { get; set; }
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
            || !agentBridge.IsAgentConnected)
        {
            return FeatureWorktreeLiveStatus.Unavailable;
        }

        try
        {
            var response = await agentBridge.SendCommandAsync(
                "GetGitChangeStatus",
                new
                {
                    workspaceRoot = featureWorkspaceRoot,
                    workspaceName = featureWorkspaceFolder,
                    repositoryName = repoName,
                    workspaceId,
                    repositoryId = repositoryId.Value,
                    includeLineStats = false
                },
                cancellationToken);

            var status = AgentResponseJson.DeserializeAgentResponse<GitChangesStatusResult>(response.Data);
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
            // non-pinned repo. A null count (Agent unreachable, no upstream, or an older Worker) is
            // unknown, never treated as zero commits pending.
            && p.EffectiveOutgoingCommits == 0
            && !p.HasUncommittedChanges
            && !p.HasStagedChanges
            && !p.HasConflicts
            // A locked worktree (D5) must be explained and unlocked with consent, never removed silently.
            && !p.IsLocked);

    /// <summary>
    /// Disk facts for one Feature worktree, from the Agent's InspectWorktree command. The App never
    /// reads repository or worktree paths from local disk directly (it can run in Docker, where those
    /// paths do not exist). Any failure to reach the Agent or parse its response is Unknown, never
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
            || !agentBridge.IsAgentConnected)
        {
            return WorktreeDiskStatus.Unknown(unknownReason);
        }

        try
        {
            var response = await agentBridge.SendCommandAsync(
                AgentHubMethods.InspectWorktree,
                new { mainRepositoryPath, worktreePath, defaultBranch, featureBranch },
                cancellationToken);
            if (!response.Success)
                return WorktreeDiskStatus.Unknown(unknownReason);

            var payload = AgentResponseJson.DeserializeAgentResponse<InspectWorktreeAgentResponse>(response.Data);
            if (payload is null || !string.IsNullOrWhiteSpace(payload.Error))
                return WorktreeDiskStatus.Unknown(unknownReason);

            return WorktreeDiskStatus.Known(
                payload.Exists, payload.IsDirty, payload.HasUpstream, payload.AheadOfUpstream, payload.AheadOfDefault,
                payload.IsLocked, payload.LockReason, payload.Branch,
                payload.FeatureBranchExists, payload.FeatureBranchAheadOfDefault,
                payload.FeatureBranchHasUpstream, payload.FeatureBranchAheadOfUpstream);
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

    private async Task SeedInitialFeatureProjectionsAsync(
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
                existing.BranchName = onTag ? null : featureBranch;
                existing.CheckedOutTag = pinnedTag;
                existing.HeadCommit = baseSha ?? src?.HeadCommit;
                existing.HasNewerTag = onTag ? src?.HasNewerTag ?? link.HasNewerTag : null;
                existing.GitVersion = src?.GitVersion ?? link.GitVersion;
                existing.Projects = src?.Projects ?? link.Projects;
                existing.RepositoryType = src?.RepositoryType ?? link.RepositoryType;
                existing.OutgoingCommits = onTag ? null : 0;
                existing.IncomingCommits = onTag ? null : src?.IncomingCommits ?? link.IncomingCommits;
                existing.DefaultBranchBehindCommits = onTag ? null : 0;
                existing.DefaultBranchAheadCommits = onTag ? null : 0;
                existing.BranchHasUpstream = onTag ? null : false;
                existing.SyncStatus = link.SyncStatus;
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
                GitVersion = src?.GitVersion ?? link.GitVersion,
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

        var existingFeatureProjects = await db.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId
                        && p.WorkspaceFeatureContextId == contextId.Value
                        && !p.IsGenerated)
            .ToListAsync(cancellationToken);
        var existingByRepoAndName = new Dictionary<(int RepositoryId, string Name), WorkspaceProject>();
        foreach (var p in existingFeatureProjects)
        {
            var key = (p.RepositoryId, p.ProjectName.Trim().ToLowerInvariant());
            existingByRepoAndName.TryAdd(key, p);
        }

        // projectIdMap: special real ProjectId -> Feature-context project (cloned or Sync-preexisting).
        var projectIdMap = new Dictionary<int, WorkspaceProject>();
        foreach (var src in realSpecialProjects)
        {
            var key = (src.RepositoryId, src.ProjectName.Trim().ToLowerInvariant());
            if (existingByRepoAndName.TryGetValue(key, out var existing))
            {
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
                ProjectName = src.ProjectName,
                ProjectType = src.ProjectType,
                ProjectFilePath = src.ProjectFilePath,
                TargetFramework = src.TargetFramework,
                PackageId = src.PackageId,
                IsGenerated = false
            };
            db.WorkspaceProjects.Add(clone);
            projectIdMap[src.ProjectId] = clone;
        }

        // Drop any Feature-scoped generated copies Sync/legacy seed may have written.
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
        }

        await db.SaveChangesAsync(cancellationToken);

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
            var projectRepo = statsScope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();
            await projectRepo.RecomputeAndPersistRepositoryDependencyStatsAsync(
                workspaceId, contextId.Value, cancellationToken);
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
        string collisionBranchName,
        CancellationToken cancellationToken)
    {
        var emptyCommits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var emptyBranches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var emptyTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var emptyCollisions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var root = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var response = await agentBridge.SendCommandAsync(
            AgentHubMethods.GetHeadCommits,
            new
            {
                workspaceId = workspace.WorkspaceId,
                workspaceName = workspace.Name,
                workspaceRoot = root,
                repositoryNames,
                collisionBranchName
            },
            cancellationToken);

        if (!response.Success)
            return (emptyCommits, emptyBranches, emptyTags, emptyCollisions);

        var payload = AgentResponseJson.DeserializeAgentResponse<GetHeadCommitsAgentResponse>(response.Data);
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
            && !IsWindowsDriveRootGraymoonPath(workspace.ManagedFeatureStorageRoot))
            return;

        var storageRoot = await workspaceService.ResolveFeatureStorageRootPathAsync(
            persistIfMissing: true,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new InvalidOperationException(
                "Feature storage root is not configured. Set it on the Settings page (or connect the Agent so the host user profile can be used as the default).");

        // Keep Agent-facing Feature storage roots Windows-shaped (App/CI may run on Linux).
        workspace.ManagedFeatureStorageRoot = string.Join('\\',
            storageRoot.Replace('/', '\\').TrimEnd('\\'),
            workspace.Name,
            "features");
    }

    /// <summary>True for <c>X:\.graymoon</c> / <c>X:\.graymoon\...</c> (Feature storage incorrectly rooted on a drive).</summary>
    private static bool IsWindowsDriveRootGraymoonPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        // "C:\.graymoon" is 12 chars; longer paths must continue with '\'.
        if (normalized.Length < 12)
            return false;
        if (!char.IsLetter(normalized[0]) || normalized[1] != ':' || normalized[2] != '\\')
            return false;
        if (!normalized.AsSpan(3).StartsWith(".graymoon", StringComparison.OrdinalIgnoreCase))
            return false;
        return normalized.Length == 12 || normalized[12] == '\\';
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

    private sealed class GetHeadCommitsAgentResponse
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

    private sealed class CreateGitWorktreeAgentResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("worktreePath")]
        public string? WorktreePath { get; set; }
    }
}
