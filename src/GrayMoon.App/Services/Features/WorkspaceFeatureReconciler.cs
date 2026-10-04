using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Agent;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Reconciles stuck Feature lifecycle rows and worktree inventory when the Worker connects.
/// Never deletes worktrees or takes a structural lock.
/// </summary>
public sealed class WorkspaceFeatureReconciler(
    IServiceScopeFactory scopeFactory,
    AgentConnectionTracker connectionTracker,
    IWorkspaceOperationLock operationLock,
    ILogger<WorkspaceFeatureReconciler> logger) : IWorkspaceFeatureReconciler, IHostedService
{
    internal const string InterruptedCreating = "Interrupted while creating";
    internal const string InterruptedRemoving = "Interrupted while removing";
    internal const string WorktreeMissing = "Worktree is missing";

    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly object _throttleLock = new();
    private DateTime _lastReconcileUtc = DateTime.MinValue;

    /// <summary>Minimum gap between reconciles. Overridable in tests.</summary>
    internal TimeSpan ReconcileMinInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Max concurrent Worker commands during one reconcile. Overridable in tests.</summary>
    internal int WorkerCallParallelism { get; set; } = 4;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        connectionTracker.OnStateChanged(OnAgentStateChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        connectionTracker.RemoveStateChanged(OnAgentStateChanged);
        return Task.CompletedTask;
    }

    private void OnAgentStateChanged(AgentConnectionState state)
    {
        if (state != AgentConnectionState.Online)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Feature reconcile failed after Worker connect");
            }
        });
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!_reconcileGate.Wait(0))
            return;

        try
        {
            lock (_throttleLock)
            {
                var elapsed = DateTime.UtcNow - _lastReconcileUtc;
                if (_lastReconcileUtc != DateTime.MinValue && elapsed < ReconcileMinInterval)
                    return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var agentBridge = scope.ServiceProvider.GetRequiredService<IAgentBridge>();
            if (!agentBridge.IsAgentConnected)
                return;

            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
            var contextResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();

            var startedAt = Stopwatch.GetTimestamp();
            logger.LogInformation("Feature Reconcile started.");
            var counts = new ReconcileCounts();
            try
            {
                counts.Interrupted = await InterruptStuckFeaturesAsync(dbFactory, cancellationToken);
                await ReconcileWorktreesAsync(dbFactory, pathResolver, contextResolver, agentBridge, counts, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Feature Reconcile finished. DurationMs={DurationMs} Outcome={Outcome}",
                    (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    ex is OperationCanceledException ? "Cancelled" : "Failed");
                throw;
            }

            logger.LogInformation(
                "Feature Reconcile finished. DurationMs={DurationMs} Outcome={Outcome} Interrupted={Interrupted} Recovered={Recovered} MissingWorktrees={MissingWorktrees} UntrackedWorktrees={UntrackedWorktrees}",
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                "Succeeded",
                counts.Interrupted,
                counts.Recovered,
                counts.MissingWorktrees,
                counts.UntrackedWorktrees);

            lock (_throttleLock)
                _lastReconcileUtc = DateTime.UtcNow;
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private sealed class ReconcileCounts
    {
        public int Interrupted;
        public int Recovered;
        public int MissingWorktrees;
        public int UntrackedWorktrees;
    }

    private async Task<int> InterruptStuckFeaturesAsync(
        IDbContextFactory<AppDbContext> dbFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stuck = await db.WorkspaceFeatures
            .Where(f => f.LifecycleState == WorkspaceFeatureLifecycleState.Creating
                || f.LifecycleState == WorkspaceFeatureLifecycleState.Removing)
            .ToListAsync(cancellationToken);

        var interrupted = 0;
        foreach (var feature in stuck)
        {
            if (operationLock.IsWorkspaceStructurallyBusy(feature.WorkspaceId))
                continue;

            var message = feature.LifecycleState == WorkspaceFeatureLifecycleState.Creating
                ? InterruptedCreating
                : InterruptedRemoving;
            feature.LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair;
            feature.LastError = message;
            feature.UpdatedAt = DateTime.UtcNow;
            interrupted++;
            logger.LogWarning(
                "Feature Reconcile marked an interrupted Feature NeedsRepair. WorkspaceId={WorkspaceId} FeatureId={FeatureId} FeatureName={FeatureName} Reason={Reason}",
                feature.WorkspaceId, feature.WorkspaceFeatureId, feature.Name, message);
        }

        if (interrupted > 0)
            await db.SaveChangesAsync(cancellationToken);
        return interrupted;
    }

    private async Task ReconcileWorktreesAsync(
        IDbContextFactory<AppDbContext> dbFactory,
        IWorkspaceContextPathResolver pathResolver,
        IWorkspaceFeatureContextResolver contextResolver,
        IAgentBridge agentBridge,
        ReconcileCounts counts,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var featureContexts = await db.WorkspaceFeatureContexts
            .AsNoTracking()
            .Include(c => c.WorkspaceFeature)
            .Include(c => c.FeatureRepositories)
            .Where(c => c.Kind == WorkspaceFeatureContextKind.Feature && c.WorkspaceFeature != null)
            .ToListAsync(cancellationToken);

        if (featureContexts.Count == 0)
            return;

        var workspaceIds = featureContexts.Select(c => c.WorkspaceId).Distinct().ToList();
        var featureIds = featureContexts
            .Where(c => c.WorkspaceFeatureId != null)
            .Select(c => c.WorkspaceFeatureId!.Value)
            .Distinct()
            .ToList();

        var workspaces = await db.Workspaces
            .AsNoTracking()
            .Where(w => workspaceIds.Contains(w.WorkspaceId))
            .ToDictionaryAsync(w => w.WorkspaceId, cancellationToken);

        var featuresById = await db.WorkspaceFeatures
            .Where(f => featureIds.Contains(f.WorkspaceFeatureId))
            .ToDictionaryAsync(f => f.WorkspaceFeatureId, cancellationToken);

        var allFeatureRepoPaths = featureContexts
            .SelectMany(c => c.FeatureRepositories)
            .Where(r => !string.IsNullOrWhiteSpace(r.WorktreePath))
            .Select(r => r.WorktreePath)
            .ToList();

        var featureRootsByFeatureId = new Dictionary<int, string>();
        foreach (var ctx in featureContexts)
        {
            if (ctx.WorkspaceFeatureId is not int featureId || ctx.WorkspaceFeature is null)
                continue;
            if (!workspaces.TryGetValue(ctx.WorkspaceId, out var workspace))
                continue;
            if (string.IsNullOrWhiteSpace(workspace.ManagedFeatureStorageRoot))
                continue;
            featureRootsByFeatureId[featureId] = AgentPath.Combine(
                workspace.ManagedFeatureStorageRoot,
                ctx.WorkspaceFeature.Name);
        }

        using var workerGate = new SemaphoreSlim(Math.Max(1, WorkerCallParallelism));
        var dirty = false;

        foreach (var workspaceGroup in featureContexts.GroupBy(c => c.WorkspaceId))
        {
            var workspaceId = workspaceGroup.Key;
            if (operationLock.IsWorkspaceStructurallyBusy(workspaceId))
                continue;

            if (!workspaces.TryGetValue(workspaceId, out var workspace))
                continue;

            var storageRoot = workspace.ManagedFeatureStorageRoot;
            var repoIds = workspaceGroup
                .SelectMany(c => c.FeatureRepositories)
                .Select(r => r.WorkspaceRepositoryId)
                .Distinct()
                .ToList();

            var special = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
            var listByRepoId = new Dictionary<int, IReadOnlyList<GitWorktreeInfo>>();

            await Parallel.ForEachAsync(
                repoIds,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, WorkerCallParallelism),
                    CancellationToken = cancellationToken,
                },
                async (repoId, ct) =>
                {
                    await workerGate.WaitAsync(ct);
                    try
                    {
                        string mainPath;
                        try
                        {
                            mainPath = await pathResolver.GetRepositoryPathAsync(special, repoId, ct);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Reconcile skipped ListGitWorktrees for workspace {WorkspaceId} repo {RepoId}", workspaceId, repoId);
                            return;
                        }

                        try
                        {
                            var listResp = await agentBridge.SendCommandAsync(
                                AgentHubMethods.ListGitWorktrees,
                                new { mainRepositoryPath = mainPath },
                                ct);
                            if (!listResp.Success || listResp.Data is null)
                                return;

                            var payload = AgentResponseJson.DeserializeAgentResponse<ListWorktreesAgentResponse>(listResp.Data);
                            var worktrees = (IReadOnlyList<GitWorktreeInfo>)(payload?.Worktrees ?? []);
                            lock (listByRepoId)
                                listByRepoId[repoId] = worktrees;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogWarning(ex, "Reconcile ListGitWorktrees failed for workspace {WorkspaceId} repo {RepoId}", workspaceId, repoId);
                        }
                    }
                    finally
                    {
                        workerGate.Release();
                    }
                });

            // An operation may have started while the Worker calls were in flight; its rows are not ours to judge.
            if (operationLock.IsWorkspaceStructurallyBusy(workspaceId))
                continue;

            // Apply row-level reconcile from successful lists only.
            foreach (var featureContext in workspaceGroup)
            {
                if (featureContext.WorkspaceFeatureId is not int featureId)
                    continue;
                if (!featuresById.TryGetValue(featureId, out var feature))
                    continue;

                foreach (var row in featureContext.FeatureRepositories)
                {
                    if (row.State is WorkspaceFeatureRepositoryState.Removing
                        or WorkspaceFeatureRepositoryState.Removed)
                    {
                        continue;
                    }

                    if (!listByRepoId.TryGetValue(row.WorkspaceRepositoryId, out var worktrees))
                        continue;

                    var registered = FindRegistered(worktrees, row.WorktreePath);

                    if (row.State is WorkspaceFeatureRepositoryState.Pending
                        or WorkspaceFeatureRepositoryState.NeedsRepair)
                    {
                        if (registered is not null)
                        {
                            var tracked = await db.WorkspaceFeatureRepositories
                                .FirstAsync(r => r.WorkspaceFeatureRepositoryId == row.WorkspaceFeatureRepositoryId, cancellationToken);
                            tracked.State = WorkspaceFeatureRepositoryState.Ready;
                            tracked.LastError = null;
                            dirty = true;
                            counts.Recovered++;
                            logger.LogDebug(
                                "Feature Reconcile found a registered worktree and marked the repository Ready. WorkspaceId={WorkspaceId} FeatureId={FeatureId} FeatureName={FeatureName} WorkspaceRepositoryId={WorkspaceRepositoryId}",
                                workspaceId, featureId, feature.Name, row.WorkspaceRepositoryId);
                        }

                        continue;
                    }

                    if (row.State != WorkspaceFeatureRepositoryState.Ready)
                        continue;

                    if (registered is not null)
                        continue;

                    // Not registered: confirm missing on disk via InspectWorktree. Any failure leaves the row alone.
                    await workerGate.WaitAsync(cancellationToken);
                    WorktreeProbe probe;
                    try
                    {
                        probe = await InspectWorktreeAsync(
                            agentBridge,
                            await SafeMainPathAsync(pathResolver, special, row.WorkspaceRepositoryId, cancellationToken),
                            row.WorktreePath,
                            cancellationToken);
                    }
                    finally
                    {
                        workerGate.Release();
                    }

                    if (probe != WorktreeProbe.Missing)
                        continue;

                    var readyRow = await db.WorkspaceFeatureRepositories
                        .FirstAsync(r => r.WorkspaceFeatureRepositoryId == row.WorkspaceFeatureRepositoryId, cancellationToken);
                    readyRow.State = WorkspaceFeatureRepositoryState.NeedsRepair;
                    readyRow.LastError = WorktreeMissing;
                    if (feature.LifecycleState == WorkspaceFeatureLifecycleState.Ready)
                    {
                        feature.LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair;
                        feature.LastError = WorktreeMissing;
                        feature.UpdatedAt = DateTime.UtcNow;
                    }
                    else if (feature.LifecycleState == WorkspaceFeatureLifecycleState.NeedsRepair
                        && string.IsNullOrWhiteSpace(feature.LastError))
                    {
                        feature.LastError = WorktreeMissing;
                        feature.UpdatedAt = DateTime.UtcNow;
                    }

                    dirty = true;
                    counts.MissingWorktrees++;
                    logger.LogWarning(
                        "Feature Reconcile found a missing worktree. WorkspaceId={WorkspaceId} FeatureId={FeatureId} FeatureName={FeatureName} WorkspaceRepositoryId={WorkspaceRepositoryId}",
                        workspaceId, featureId, feature.Name, row.WorkspaceRepositoryId);
                }
            }

            // Untracked worktrees under the managed Feature storage root.
            if (string.IsNullOrWhiteSpace(storageRoot))
                continue;

            var seenUntracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var worktrees in listByRepoId.Values)
            {
                foreach (var wt in worktrees)
                {
                    if (string.IsNullOrWhiteSpace(wt.WorktreePath) || wt.IsBare)
                        continue;
                    if (!IsPathUnder(wt.WorktreePath, storageRoot))
                        continue;
                    if (allFeatureRepoPaths.Any(p => PathsEqualNormalized(p, wt.WorktreePath)))
                        continue;

                    var pathKey = NormalizePathKey(wt.WorktreePath);
                    if (!seenUntracked.Add(pathKey))
                        continue;

                    counts.UntrackedWorktrees++;
                    var owningFeatureId = featureRootsByFeatureId
                        .FirstOrDefault(kv => IsPathUnder(wt.WorktreePath, kv.Value))
                        .Key;

                    if (owningFeatureId != 0 && featuresById.TryGetValue(owningFeatureId, out var owningFeature))
                    {
                        owningFeature.LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair;
                        owningFeature.LastError = UntrackedWorktreeError(wt.WorktreePath);
                        owningFeature.UpdatedAt = DateTime.UtcNow;
                        dirty = true;
                    }
                    else
                    {
                        logger.LogWarning(
                            "Untracked worktree under Feature storage root with no Feature record: {WorktreePath}",
                            wt.WorktreePath);
                    }
                }
            }
        }

        if (dirty)
            await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<string?> SafeMainPathAsync(
        IWorkspaceContextPathResolver pathResolver,
        WorkspaceFeatureContextId special,
        int workspaceRepositoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await pathResolver.GetRepositoryPathAsync(special, workspaceRepositoryId, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static GitWorktreeInfo? FindRegistered(IReadOnlyList<GitWorktreeInfo> worktrees, string? expectedPath)
    {
        if (string.IsNullOrWhiteSpace(expectedPath))
            return null;

        foreach (var wt in worktrees)
        {
            if (PathsEqualNormalized(wt.WorktreePath, expectedPath))
                return wt;
        }

        return null;
    }

    private async Task<WorktreeProbe> InspectWorktreeAsync(
        IAgentBridge agentBridge,
        string? mainRepositoryPath,
        string? worktreePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mainRepositoryPath) || string.IsNullOrWhiteSpace(worktreePath))
            return WorktreeProbe.Unknown;

        try
        {
            var response = await agentBridge.SendCommandAsync(
                AgentHubMethods.InspectWorktree,
                new { mainRepositoryPath, worktreePath },
                cancellationToken);
            if (!response.Success)
                return WorktreeProbe.Unknown;

            if (IsUnknownCommandError(response.Error))
                return WorktreeProbe.Unknown;

            var payload = AgentResponseJson.DeserializeAgentResponse<InspectWorktreeAgentResponse>(response.Data);
            if (payload is null || !string.IsNullOrWhiteSpace(payload.Error))
                return WorktreeProbe.Unknown;

            return payload.Exists ? WorktreeProbe.Exists : WorktreeProbe.Missing;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "InspectWorktree failed during reconcile for {Path}", worktreePath);
            return WorktreeProbe.Unknown;
        }
    }

    internal static string NormalizePathKey(string path) => AgentPath.Normalize(path);

    internal static bool PathsEqualNormalized(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return string.Equals(NormalizePathKey(left), NormalizePathKey(right), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPathUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;

        var p = NormalizePathKey(path);
        var r = NormalizePathKey(root);
        var sep = AgentPath.IsPosix(r) ? '/' : '\\';
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + sep, StringComparison.OrdinalIgnoreCase);
    }

    internal static string UntrackedWorktreeError(string path) =>
        $"Worktree {path} has no record in GrayMoon";

    private static bool IsUnknownCommandError(string? error) =>
        !string.IsNullOrWhiteSpace(error) && error.Contains("Unknown command", StringComparison.OrdinalIgnoreCase);

    private enum WorktreeProbe
    {
        Unknown,
        Exists,
        Missing,
    }

    private sealed class ListWorktreesAgentResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("worktrees")]
        public List<GitWorktreeInfo>? Worktrees { get; set; }
    }
}
