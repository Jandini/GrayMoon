using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Agent;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Features;

public sealed class WorkspaceFeatureOperations(
    IDbContextFactory<AppDbContext> dbContextFactory,
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

        EnsureManagedFeatureStorageRoot(workspace);
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
        var heads = await GetHeadCommitsAsync(workspace, repoNames, cancellationToken);
        if (heads.Count != repoNames.Count)
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
            if (!heads.TryGetValue(repoName, out var sha) || string.IsNullOrWhiteSpace(sha))
                return FailCreate("HeadCommitsIncomplete", $"Missing HEAD for repository '{repoName}'.");

            var worktreePath = await pathResolver.GetRepositoryPathAsync(contextId, link.WorkspaceRepositoryId, cancellationToken);
            var row = new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                WorktreePath = worktreePath,
                BaseCommitSha = sha,
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

        var plans = new List<RemoveFeatureRepositoryPlan>();
        foreach (var row in rows)
        {
            var state = states.FirstOrDefault(s => s.WorkspaceRepositoryId == row.WorkspaceRepositoryId);
            var pr = prs.FirstOrDefault(p => p.WorkspaceRepositoryId == row.WorkspaceRepositoryId);
            var exists = Directory.Exists(row.WorktreePath);
            plans.Add(new RemoveFeatureRepositoryPlan
            {
                WorkspaceRepositoryId = row.WorkspaceRepositoryId,
                RepositoryName = row.WorkspaceRepository?.Repository?.RepositoryName ?? "",
                WorktreePath = row.WorktreePath,
                WorktreeExists = exists,
                BranchName = state?.BranchName ?? info.FeatureName,
                HeadCommit = state?.HeadCommit,
                HasUncommittedChanges = false,
                HasStagedChanges = false,
                HasConflicts = false,
                OutgoingCommits = state?.OutgoingCommits,
                HasUpstream = state?.BranchHasUpstream == true,
                PullRequestNumber = pr?.PullRequestNumber,
                PullRequestState = pr?.State,
                PullRequestMerged = pr?.MergedAt is not null,
                Warning = row.State == WorkspaceFeatureRepositoryState.NeedsRepair ? row.LastError : null
            });
        }

        var classification = Classify(plans);
        var safe = classification is RemoveFeatureClassification.Completed
                   && plans.All(p => (p.OutgoingCommits ?? 0) == 0);

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
                    await RemoveFeatureCoreAsync(featureContextId, info, options, progress, linked.Token);
                    tcs.TrySetResult(OperationResult.Ok());
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

    private async Task RemoveFeatureCoreAsync(
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

        foreach (var row in rows)
        {
            progress?.Report(new OperationProgress($"Removing worktree {row.WorktreePath}..."));
            var mainPath = await pathResolver.GetRepositoryPathAsync(
                specialContextId, row.WorkspaceRepositoryId, cancellationToken);
            var force = options.AllowDiscardUncommitted || options.AllowForceDeleteLocalBranches;
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
                return;
            }
        }

        var selected = await selectedContextService.GetSelectedAsync(info.WorkspaceId, cancellationToken);
        if (selected?.Value == featureContextId.Value)
            await selectedContextService.SetSelectedAsync(info.WorkspaceId, specialContextId, cancellationToken);

        db.WorkspaceFeatureRepositories.RemoveRange(rows);
        var context = await db.WorkspaceFeatureContexts
            .FirstAsync(c => c.WorkspaceFeatureContextId == featureContextId.Value, cancellationToken);
        db.WorkspaceFeatureContexts.Remove(context);
        db.WorkspaceFeatures.Remove(feature);
        await db.SaveChangesAsync(cancellationToken);
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

        foreach (var link in links)
        {
            specialStates.TryGetValue(link.WorkspaceRepositoryId, out var src);
            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                BranchName = featureBranch,
                HeadCommit = src?.HeadCommit,
                GitVersion = src?.GitVersion,
                Projects = src?.Projects,
                RepositoryType = src?.RepositoryType,
                OutgoingCommits = 0,
                IncomingCommits = src?.IncomingCommits,
                DefaultBranchBehindCommits = src?.DefaultBranchBehindCommits,
                DefaultBranchAheadCommits = src?.DefaultBranchAheadCommits,
                BranchHasUpstream = false,
                SyncStatus = src?.SyncStatus ?? RepoSyncStatus.NeedsSync,
                OutOfDateFileRepos = src?.OutOfDateFileRepos,
                TotalFileConfigRepos = src?.TotalFileConfigRepos
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
    }

    private async Task<Dictionary<string, string>> GetHeadCommitsAsync(
        Workspace workspace,
        IReadOnlyList<string> repositoryNames,
        CancellationToken cancellationToken)
    {
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
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var payload = AgentResponseJson.DeserializeAgentResponse<GetHeadCommitsAgentResponse>(response.Data);
        return payload?.Commits ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void EnsureManagedFeatureStorageRoot(Workspace workspace)
    {
        if (!string.IsNullOrWhiteSpace(workspace.ManagedFeatureStorageRoot))
            return;

        var parent = Path.GetDirectoryName((workspace.RootPath ?? string.Empty).Replace('/', '\\'));
        if (string.IsNullOrWhiteSpace(parent))
            parent = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        workspace.ManagedFeatureStorageRoot = Path.Combine(parent!, ".graymoon", workspace.Name, "features");
    }

    private static RemoveFeatureClassification Classify(IReadOnlyList<RemoveFeatureRepositoryPlan> plans)
    {
        if (plans.Any(p => p.Warning is not null || !p.WorktreeExists))
            return RemoveFeatureClassification.NeedsRepair;
        if (plans.All(p => p.PullRequestMerged == true))
            return RemoveFeatureClassification.Completed;
        if (plans.Any(p => string.Equals(p.PullRequestState, "closed", StringComparison.OrdinalIgnoreCase)
                           && p.PullRequestMerged != true))
            return RemoveFeatureClassification.Abandoned;
        return RemoveFeatureClassification.Active;
    }

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
