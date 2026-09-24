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
    ILogger<WorkspaceFeatureOperations> logger) : IWorkspaceFeatureOperations
{
    private static readonly Regex BranchNamePattern = new(
        @"^(?!.*\.\.)(?!/)(?!.*/$)(?!.*//)(?!.*[@{])[^\s~^:?*\[\\]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
                    progress?.Report(new OperationProgress(op.DisplayMessage));
                    var result = await CreateFeatureCoreAsync(workspaceId, name, progress, linked.Token);
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

        progress?.Report(new OperationProgress("Reading Workspace HEAD commits..."));
        var snapshot = await GetHeadSnapshotAsync(workspace, repoNames, cancellationToken);
        if (snapshot.Commits.Count != repoNames.Count)
            return FailCreate("HeadCommitsIncomplete", "Could not resolve HEAD for every Workspace repository.");

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

            var worktreePath = await pathResolver.GetRepositoryPathAsync(contextId, link.WorkspaceRepositoryId, cancellationToken);
            var row = new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                WorktreePath = worktreePath,
                BaseCommitSha = sha,
                ParentBranchName = parentBranch,
                CreatedAt = now,
                State = WorkspaceFeatureRepositoryState.Pending
            };
            db.WorkspaceFeatureRepositories.Add(row);
            pendingRows.Add(row);
        }

        await db.SaveChangesAsync(cancellationToken);

        progress?.Report(new OperationProgress("Creating worktrees..."));
        var anyFailure = 0;
        using var gate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount));
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
                        branchName = name,
                        baseCommitSha = row.BaseCommitSha
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

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Include(r => r.WorkspaceRepository)!.ThenInclude(l => l!.Repository)
            .Where(r => r.WorkspaceFeatureContextId == featureContextId.Value)
            .ToListAsync(cancellationToken);

        var prs = await db.WorkspaceRepositoryContextPullRequests
            .AsNoTracking()
            .Where(p => p.WorkspaceFeatureContextId == featureContextId.Value)
            .ToListAsync(cancellationToken);

        var states = await db.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == featureContextId.Value)
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

        var plans = new List<RemoveFeatureRepositoryPlan>();
        foreach (var row in rows)
        {
            var state = states.FirstOrDefault(s => s.WorkspaceRepositoryId == row.WorkspaceRepositoryId);
            var pr = prs.FirstOrDefault(p => p.WorkspaceRepositoryId == row.WorkspaceRepositoryId);
            var exists = Directory.Exists(row.WorktreePath);
            var live = await ProbeFeatureWorktreeLiveStatusAsync(
                featureWorkspaceRoot,
                featureWorkspaceFolder,
                info.WorkspaceId,
                row,
                exists,
                cancellationToken);

            plans.Add(new RemoveFeatureRepositoryPlan
            {
                WorkspaceRepositoryId = row.WorkspaceRepositoryId,
                RepositoryName = row.WorkspaceRepository?.Repository?.RepositoryName ?? "",
                WorktreePath = row.WorktreePath,
                WorktreeExists = exists,
                BranchName = state?.BranchName ?? info.FeatureName,
                HeadCommit = live.HeadCommit ?? state?.HeadCommit,
                HasUncommittedChanges = live.HasUncommittedChanges,
                HasStagedChanges = live.HasStagedChanges,
                HasConflicts = live.HasConflicts,
                LiveStatusEstablished = live.LiveStatusEstablished,
                OutgoingCommits = state?.OutgoingCommits,
                HasUpstream = state?.BranchHasUpstream == true,
                PullRequestNumber = pr?.PullRequestNumber,
                PullRequestState = pr?.State,
                PullRequestMerged = pr?.MergedAt is not null,
                Warning = ComposeRemoveWarning(row, live)
            });
        }

        var classification = Classify(plans);
        var safe = IsAutomaticallySafe(classification, plans);

        return new RemoveFeaturePlan
        {
            Success = true,
            Classification = classification,
            Repositories = plans,
            IsAutomaticallySafe = safe,
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

        if (!plan.IsAutomaticallySafe
            && !options.AllowDiscardUncommitted
            && !options.AllowForceDeleteLocalBranches)
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
                    progress?.Report(new OperationProgress(op.DisplayMessage));
                    var outcome = await RemoveFeatureCoreAsync(featureContextId, info, options, progress, linked.Token);
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
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var feature = await db.WorkspaceFeatures
            .FirstAsync(f => f.WorkspaceFeatureId == info.WorkspaceFeatureId, cancellationToken);
        feature.LifecycleState = WorkspaceFeatureLifecycleState.Removing;
        feature.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var specialContextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(info.WorkspaceId, cancellationToken);
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
        var repositoryIds = links.Select(l => l.RepositoryId).Distinct().ToList();

        foreach (var row in rows)
        {
            progress?.Report(new OperationProgress($"Removing worktree {row.WorktreePath}..."));
            var mainPath = await pathResolver.GetRepositoryPathAsync(
                specialContextId, row.WorkspaceRepositoryId, cancellationToken);
            // Force worktree remove only when the user authorized discarding dirty Feature files.
            // AllowForceDeleteLocalBranches does not imply discard permission.
            var force = options.AllowDiscardUncommitted;
            var response = await agentBridge.SendCommandAsync(
                AgentHubMethods.RemoveGitWorktree,
                new { mainRepositoryPath = mainPath, worktreePath = row.WorktreePath, force },
                cancellationToken);
            if (!response.Success)
            {
                logger.LogWarning(
                    "RemoveGitWorktree failed for {Path}: {Error}",
                    row.WorktreePath, response.Error);
                row.State = WorkspaceFeatureRepositoryState.NeedsRepair;
                row.LastError = response.Error;
                feature.LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair;
                feature.LastError = response.Error;
                feature.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                // Do not continue into branch delete or the Workspace refresh, and do not report
                // success: the UI would navigate back to Workspace while the stored branch is still
                // the Feature. The worktree is still registered, so the Feature stays for repair.
                return OperationResult.Fail(
                    string.IsNullOrWhiteSpace(response.Error)
                        ? $"Failed to remove worktree {row.WorktreePath}."
                        : response.Error);
            }

            // §27.8: after worktree remove, delete the Feature branch from the main repository.
            var branchName = info.FeatureName;
            if (!string.IsNullOrWhiteSpace(branchName)
                && linkByWrId.TryGetValue(row.WorkspaceRepositoryId, out var link)
                && !string.IsNullOrWhiteSpace(link.Repository?.RepositoryName))
            {
                var repoName = link.Repository!.RepositoryName;
                progress?.Report(new OperationProgress($"Deleting local branch {branchName}..."));
                var (workspaceRoot, workspaceFolderName) =
                    await pathResolver.GetAgentWorkspaceArgsAsync(specialContextId, cancellationToken);
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
                if (!deleteLocal.Success)
                {
                    logger.LogWarning(
                        "Delete local Feature branch {Branch} failed for {Repo}: {Error}",
                        branchName, repoName, deleteLocal.Error);
                }

                if (options.DeleteRemoteBranches)
                {
                    progress?.Report(new OperationProgress($"Deleting remote branch {branchName}..."));
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

        db.WorkspaceFeatureRepositories.RemoveRange(rows);
        var context = await db.WorkspaceFeatureContexts
            .FirstAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value, cancellationToken);
        db.WorkspaceFeatureContexts.Remove(context);
        db.WorkspaceFeatures.Remove(feature);
        await db.SaveChangesAsync(cancellationToken);
        return OperationResult.Ok();
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

    private static string? ComposeRemoveWarning(WorkspaceFeatureRepository row, FeatureWorktreeLiveStatus live)
    {
        if (row.State == WorkspaceFeatureRepositoryState.NeedsRepair && !string.IsNullOrWhiteSpace(row.LastError))
            return row.LastError;
        if (!live.LiveStatusEstablished && Directory.Exists(row.WorktreePath))
            return "Live worktree status could not be established";
        return null;
    }

    private static bool IsAutomaticallySafe(
        RemoveFeatureClassification classification,
        IReadOnlyList<RemoveFeatureRepositoryPlan> plans) =>
        classification is RemoveFeatureClassification.Completed
        && plans.All(p =>
            p.LiveStatusEstablished
            && (p.OutgoingCommits ?? 0) == 0
            && !p.HasUncommittedChanges
            && !p.HasStagedChanges
            && !p.HasConflicts);

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

    public async Task EnsureNoFeaturesBeforeMembershipChangeAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId, cancellationToken))
        {
            throw new InvalidOperationException(
                "Cannot add or remove Workspace repositories while Features exist. Remove Features first (or use a full transactional fan-out).");
        }
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

        // Workspace grid SyncStatus / GitVersion come from the link for the special context
        // (Project() reads wr.SyncStatus). Feature grids read context state instead, so seed from
        // the link as the source of truth — special-state SyncStatus can lag and left new Features all-red.
        foreach (var link in links)
        {
            specialStates.TryGetValue(link.WorkspaceRepositoryId, out var src);
            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                BranchName = featureBranch,
                CheckedOutTag = src?.CheckedOutTag ?? link.CheckedOutTag,
                HeadCommit = src?.HeadCommit,
                HasNewerTag = src?.HasNewerTag ?? link.HasNewerTag,
                GitVersion = src?.GitVersion ?? link.GitVersion,
                Projects = src?.Projects ?? link.Projects,
                RepositoryType = src?.RepositoryType ?? link.RepositoryType,
                OutgoingCommits = 0,
                IncomingCommits = src?.IncomingCommits ?? link.IncomingCommits,
                DefaultBranchBehindCommits = src?.DefaultBranchBehindCommits ?? link.DefaultBranchBehindCommits,
                DefaultBranchAheadCommits = src?.DefaultBranchAheadCommits ?? link.DefaultBranchAheadCommits,
                BranchHasUpstream = false,
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

        var projectIdMap = new Dictionary<int, WorkspaceProject>();
        foreach (var src in specialProjects)
        {
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
                IsGenerated = src.IsGenerated
            };
            db.WorkspaceProjects.Add(clone);
            projectIdMap[src.ProjectId] = clone;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (projectIdMap.Count > 0)
        {
            var srcIds = projectIdMap.Keys.ToList();
            var deps = await db.ProjectDependencies
                .AsNoTracking()
                .Where(d => srcIds.Contains(d.DependentProjectId) || srcIds.Contains(d.ReferencedProjectId))
                .ToListAsync(cancellationToken);
            foreach (var dep in deps)
            {
                if (!projectIdMap.TryGetValue(dep.DependentProjectId, out var depClone))
                    continue;
                if (!projectIdMap.TryGetValue(dep.ReferencedProjectId, out var refClone))
                    continue;
                db.ProjectDependencies.Add(new ProjectDependency
                {
                    DependentProjectId = depClone.ProjectId,
                    ReferencedProjectId = refClone.ProjectId,
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

    private async Task<(Dictionary<string, string> Commits, Dictionary<string, string> Branches)> GetHeadSnapshotAsync(
        Workspace workspace,
        IReadOnlyList<string> repositoryNames,
        CancellationToken cancellationToken)
    {
        var emptyCommits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var emptyBranches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var response = await agentBridge.SendCommandAsync(
            AgentHubMethods.GetHeadCommits,
            new
            {
                workspaceId = workspace.WorkspaceId,
                workspaceName = workspace.Name,
                workspaceRoot = root,
                repositoryNames
            },
            cancellationToken);

        if (!response.Success)
            return (emptyCommits, emptyBranches);

        var payload = AgentResponseJson.DeserializeAgentResponse<GetHeadCommitsAgentResponse>(response.Data);
        return (
            payload?.Commits ?? emptyCommits,
            payload?.Branches ?? emptyBranches);
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
    /// True when the repo's PR is merged, or no PR was ever opened (null/empty number and state).
    /// Fresh Features with zero commits fall into the latter bucket.
    /// </summary>
    private static bool IsPrMergedOrNeverCreated(RemoveFeatureRepositoryPlan p) =>
        p.PullRequestMerged == true
        || (p.PullRequestNumber is null or 0 && string.IsNullOrWhiteSpace(p.PullRequestState));

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
