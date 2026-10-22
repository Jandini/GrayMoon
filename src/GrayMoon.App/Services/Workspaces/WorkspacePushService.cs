using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Ci;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Push workflow implementation for a workspace.
/// This service exists to isolate push-specific logic from the much larger <see cref="WorkspaceGitService"/>.
/// Stateless (no UI state); caller owns CTS / progress / toast.
/// </summary>
public sealed partial class WorkspacePushService(
    IWorkerBridge workerBridge,
    WorkspaceService workspaceService,
    WorkspaceRepository workspaceRepository,
    WorkspaceDependencyService workspaceDependencyService,
    WorkspaceProjectRepository workspaceProjectRepository,
    WorkspaceRepositoryStateWriter stateWriter,
    WorkspaceStateRecomputeScope recomputeScope,
    AppDbContext dbContext,
    Microsoft.Extensions.Options.IOptions<WorkspaceOptions> workspaceOptions,
    IWorkspaceFeatureContextResolver contextResolver,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceCapabilitiesResolver capabilitiesResolver,
    IServiceScopeFactory scopeFactory,
    ILogger<WorkspacePushService> logger,
    IHubContext<WorkspaceSyncHub>? hubContext = null,
    PackageRegistrySyncService? packageRegistrySyncService = null,
    NuGetService? nuGetService = null,
    ConnectorRepository? connectorRepository = null,
    ConnectorHealthService? connectorHealthService = null,
    IWorkspaceCiProviderResolver? ciProviderResolver = null,
    OverlayCommandTerminalService? overlayCommandTerminalService = null)
{
    private readonly IWorkerBridge _workerBridge = workerBridge ?? throw new ArgumentNullException(nameof(workerBridge));
    private readonly WorkspaceService _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
    private readonly WorkspaceRepository _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
    private readonly WorkspaceDependencyService _workspaceDependencyService = workspaceDependencyService ?? throw new ArgumentNullException(nameof(workspaceDependencyService));
    private readonly WorkspaceProjectRepository _workspaceProjectRepository = workspaceProjectRepository ?? throw new ArgumentNullException(nameof(workspaceProjectRepository));
    private readonly WorkspaceRepositoryStateWriter _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
    private readonly WorkspaceStateRecomputeScope _recomputeScope = recomputeScope ?? throw new ArgumentNullException(nameof(recomputeScope));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IWorkspaceFeatureContextResolver _contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
    private readonly IWorkspaceContextPathResolver _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
    private readonly IWorkspaceCapabilitiesResolver _capabilitiesResolver = capabilitiesResolver ?? throw new ArgumentNullException(nameof(capabilitiesResolver));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly ILogger<WorkspacePushService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly int _maxConcurrent = Math.Max(1, workspaceOptions?.Value?.MaxParallelOperations ?? 16);
    private readonly IHubContext<WorkspaceSyncHub>? _hubContext = hubContext;
    private readonly PackageRegistrySyncService? _packageRegistrySyncService = packageRegistrySyncService;
    private readonly NuGetService? _nuGetService = nuGetService;
    private readonly ConnectorRepository? _connectorRepository = connectorRepository;
    private readonly ConnectorHealthService? _connectorHealthService = connectorHealthService;
    private readonly IWorkspaceCiProviderResolver? _ciProviderResolver = ciProviderResolver;
    private readonly OverlayCommandTerminalService? _overlayCommandTerminalService = overlayCommandTerminalService;

    /// <summary>Gets the push plan for the special Workspace context: all workspace repos by dependency level. Legacy overload for callers without a context id.</summary>
    public async Task<(IReadOnlyList<PushRepoPayload> Payload, bool IsMultiLevel)> GetPushPlanAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var contextId = await _contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        return await GetPushPlanAsync(workspaceId, contextId.Value, cancellationToken);
    }

    /// <summary>Gets the push plan scoped to <paramref name="workspaceFeatureContextId"/>: that context's own repos by dependency level. Used to show multi-level push dialog and push with dependency synchronization.</summary>
    public async Task<(IReadOnlyList<PushRepoPayload> Payload, bool IsMultiLevel)> GetPushPlanAsync(int workspaceId, int workspaceFeatureContextId, CancellationToken cancellationToken = default)
    {
        var payload = await _workspaceDependencyService.GetPushPlanPayloadAsync(workspaceId, workspaceFeatureContextId, cancellationToken);
        if (payload.Count == 0)
            return (payload, false);
        var levels = payload.Select(p => p.DependencyLevel ?? 0).Distinct().ToList();
        var isMultiLevel = levels.Count > 1;
        return (payload, isMultiLevel);
    }

    /// <summary>Gets the plain push list scoped to <paramref name="workspaceFeatureContextId"/>: every non-tag-pinned repo, without dependency levels or required packages.</summary>
    public async Task<IReadOnlyList<PushRepoPayload>> GetPushPayloadWithoutDependenciesAsync(int workspaceId, int workspaceFeatureContextId, CancellationToken cancellationToken = default)
        => await _workspaceProjectRepository.GetPushPayloadWithoutDependenciesAsync(workspaceId, workspaceFeatureContextId, cancellationToken);

    /// <summary>
    /// Runs dependency-synchronized push: sync package registries (unless already done by caller), then push by level (lowest first).
    /// For each level, waits until required packages are in registry (or pushes all at once if not possible), then pushes all repos at that level in parallel, then restores locally so the next level's wait starts after restore.
    /// Ensures branch is upstreamed even when there are no commits to push.
    /// When <paramref name="repoIdsToPush"/> is set, only those repos are pushed.
    /// Set <paramref name="packageRegistriesAlreadySynced"/> to true when the caller already synced required packages
    /// (e.g. via SyncRegistriesForPackageIdsAsync) to avoid syncing twice.
    /// Set <paramref name="restorePackages"/> to false to skip the per-level local restore (the push itself and
    /// the NuGet-availability wait between levels are unaffected).
    /// </summary>
    public async Task RunPushAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int>? repoIdsToPush = null,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        Action<int, string>? onLevelError = null,
        Action? onAppSideComplete = null,
        bool packageRegistriesAlreadySynced = false,
        IReadOnlySet<int>? syncedRepoIds = null,
        CancellationToken cancellationToken = default,
        string? runId = null,
        bool restorePackages = true)
    {
        runId = GrayMoon.Abstractions.Logging.GrayMoonOperationContext.EnsureId(runId);
        _logger.LogInformation(
            "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: RunPushAsync starting. Scope={Scope}",
            runId, workspaceId, repoIdsToPush == null ? "all repos" : $"{repoIdsToPush.Count} repo(s): [{string.Join(",", repoIdsToPush)}]");

        if (!_workerBridge.IsWorkerConnected)
            throw new InvalidOperationException("Worker not connected. Start the GrayMoon Worker to push.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var workerArgs = await ResolveWorkerPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);
        var workspaceRoot = workerArgs.WorkspaceRoot;
        var workspaceFolderName = workerArgs.WorkspaceFolderName;
        var workspaceRepositoryName = workerArgs.WorkspaceRepositoryName;
        var configuredRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        await _workspaceService.CreateDirectoryAsync(workspace.Name, configuredRoot, cancellationToken);

        if (!packageRegistriesAlreadySynced)
        {
            onProgressMessage?.Invoke("Syncing package registries...");
            if (_packageRegistrySyncService != null)
                await _packageRegistrySyncService.SyncWorkspacePackageRegistriesAsync(workspaceId, cancellationToken: cancellationToken);
        }

        var fullPayload = await _workspaceDependencyService.GetPushPlanPayloadAsync(workspaceId, contextId.Value, cancellationToken);
        var payload = repoIdsToPush is { Count: > 0 }
            ? fullPayload.Where(p => repoIdsToPush.Contains(p.RepoId)).ToList()
            : fullPayload;
        if (payload.Count == 0)
        {
            onProgressMessage?.Invoke("No repositories to push.");
            return;
        }

        var links = await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .ThenInclude(r => r!.Connector)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        var bearerByRepoId = links
            .Where(wr => wr.Repository != null)
            .ToDictionary(
                wr => wr.RepositoryId,
                wr => ConnectorHelpers.UnprotectToken(wr.Repository!.Connector?.UserToken));

        var tagPinnedRepoIdsPush = links
            .Where(wr => !string.IsNullOrWhiteSpace(wr.CheckedOutTag))
            .Select(wr => wr.RepositoryId)
            .ToHashSet();
        if (tagPinnedRepoIdsPush.Count > 0)
            payload = payload.Where(p => !tagPinnedRepoIdsPush.Contains(p.RepoId)).ToList();
        if (payload.Count == 0)
        {
            onProgressMessage?.Invoke("No repositories to push.");
            return;
        }

        bool synchronizedPushPossible = payload.All(p => p.RequiredPackages.All(r => r.MatchedConnectorId.HasValue));
        var missingPackagesCount = payload
            .SelectMany(p => p.RequiredPackages)
            .Where(r => !r.MatchedConnectorId.HasValue)
            .DistinctBy(r => (r.PackageId, r.Version))
            .Count();
        if (!synchronizedPushPossible && missingPackagesCount > 0)
        {
            _logger.LogInformation("Push: synchronized push unavailable; {Count} required package mappings are missing.", missingPackagesCount);
            throw new SynchronizedPushNotPossibleException(missingPackagesCount);
        }

        if (!synchronizedPushPossible || _nuGetService == null || _connectorRepository == null)
        {
            onProgressMessage?.Invoke("Pushing all repositories...");
            await PushReposAsync(workspace, contextId, payload, bearerByRepoId, onProgressMessage, onRepoError, onAppSideComplete, cancellationToken: cancellationToken);
            await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, contextId, payload, cancellationToken);
            return;
        }

        var levelsAsc = payload.Select(p => p.DependencyLevel ?? 0).Distinct().OrderBy(x => x).ToList();
        var lastLevel = levelsAsc[^1];
        var ciProvider = _ciProviderResolver == null
            ? NoCiProvider.Instance
            : await _ciProviderResolver.GetForWorkspaceAsync(workspaceId, cancellationToken);
        var run = new SynchronizedPushRun(
            workspace,
            contextId,
            workspaceRoot,
            workspaceRepositoryName,
            links,
            bearerByRepoId,
            ciProvider,
            restorePackages,
            syncedRepoIds,
            RestoreOnlySyncedRepos: false,
            runId,
            onProgressMessage,
            onRepoError,
            onLevelError,
            onAppSideComplete)
        {
            FeatureLabel = await ResolveFeatureLabelAsync(workspaceId, contextId, cancellationToken)
        };
        run.Diagnostics.RememberPlan(payload);
        _logger.LogInformation(
            "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: {LevelCount} level(s) to push: {Levels}",
            runId, workspaceId, levelsAsc.Count,
            string.Join(", ", levelsAsc.Select(l => $"L{l}={payload.Count(p => (p.DependencyLevel ?? 0) == l)} repo(s)")));
        await LogPushInvocationAsync(run, payload, cancellationToken);
        foreach (var level in levelsAsc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reposAtLevel = payload.Where(p => (p.DependencyLevel ?? 0) == level).ToList();
            if (reposAtLevel.Count == 0) continue;
            if (!await PushLevelAsync(run, level, reposAtLevel, level == lastLevel, levelsAsc.Count, skippedCount: 0, cancellationToken))
            {
                LogOperationSummary(run, pushSucceeded: false);
                return;
            }
        }

        await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, contextId, payload, cancellationToken);

        _logger.LogInformation(
            "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: RunPushAsync finished. TotalPushed={TotalPushed}",
            runId, workspaceId, run.PushedRepos.Count);
        LogOperationSummary(run, pushSucceeded: true);
    }

    /// <summary>
    /// Everything one synchronized push needs to push a level. <see cref="RunPushAsync"/> and the push lane both
    /// build one and hand levels to <see cref="PushLevelAsync"/>, so a level is waited for, pushed and restored the
    /// same way however it was reached.
    /// </summary>
    private sealed record SynchronizedPushRun(
        Workspace Workspace,
        WorkspaceFeatureContextId ContextId,
        string? WorkspaceRoot,
        string? WorkspaceRepositoryName,
        IReadOnlyList<WorkspaceRepositoryLink> Links,
        IReadOnlyDictionary<int, string?> BearerByRepoId,
        IWorkspaceCiProvider CiProvider,
        bool RestorePackages,
        IReadOnlySet<int>? SyncedRepoIds,
        bool RestoreOnlySyncedRepos,
        string? RunId,
        Action<string>? OnProgressMessage,
        Action<int, string>? OnRepoError,
        Action<int, string>? OnLevelError,
        Action? OnAppSideComplete)
    {
        /// <summary>Repositories pushed by the levels already finished; the CI watch reads it while the next level waits.</summary>
        public List<PushRepoPayload> PushedRepos { get; } = [];

        /// <summary>Feature name when the context is a Feature; otherwise Workspace. Diagnostic only.</summary>
        public string FeatureLabel { get; init; } = "Unknown";

        /// <summary>Level outcomes already produced by this run, so the closing summary can name them.</summary>
        public PushRunDiagnostics Diagnostics { get; } = new();
    }

    /// <summary>
    /// Waits for the level's required packages, pushes <paramref name="reposAtLevel"/>, records their commit counts and
    /// restores them. Returns false when the level failed or timed out (already reported through the run's callbacks),
    /// which stops the synchronized push; true when the next level may start.
    /// </summary>
    private async Task<bool> PushLevelAsync(
        SynchronizedPushRun run,
        int level,
        IReadOnlyList<PushRepoPayload> reposAtLevel,
        bool isLastLevel,
        int totalLevels,
        int skippedCount,
        CancellationToken cancellationToken)
    {
        var levelClock = System.Diagnostics.Stopwatch.StartNew();
        var workspace = run.Workspace;
        var workspaceId = workspace.WorkspaceId;
        var contextId = run.ContextId;
        var workspaceRoot = run.WorkspaceRoot;
        var workspaceRepositoryName = run.WorkspaceRepositoryName;
        var links = run.Links;
        var bearerByRepoId = run.BearerByRepoId;
        var ciProvider = run.CiProvider;
        var syncedRepoIds = run.SyncedRepoIds;
        var restorePackages = run.RestorePackages;
        var runId = run.RunId;
        var onRepoError = run.OnRepoError;
        var onLevelError = run.OnLevelError;
        var pushedRepos = run.PushedRepos;
        var onProgressMessage = run.OnProgressMessage;
        var levelProgress = onProgressMessage == null ? (Action<string>?)null : msg => onProgressMessage($"{msg}\nLevel {level}");
        var totalDeps = 0;

        void LogLevelOutcome(bool succeeded, int pushed, int failed, int blocked, string? category)
        {
            if (succeeded)
                run.Diagnostics.CompletedLevels.Add(level);
            else if (run.Diagnostics.FailedLevel == null)
            {
                run.Diagnostics.FailedLevel = level;
                run.Diagnostics.FailureCategory = category;
            }

            PackageWaitDiagnostics.LogLevelOutcome(
                _logger,
                runId,
                workspaceId,
                level,
                totalLevels,
                reposAtLevel.Count,
                pushed,
                failed,
                skippedCount,
                blocked,
                totalDeps,
                levelClock.Elapsed.TotalSeconds,
                succeeded,
                category);
        }

        _logger.LogInformation(
            "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: starting. RepoIds=[{RepoIds}]",
            runId, workspaceId, level, string.Join(",", reposAtLevel.Select(r => r.RepoId)));

        var requiredForLevel = reposAtLevel
            .SelectMany(r => r.RequiredPackages)
            .DistinctBy(r => (r.PackageId, r.Version, r.MatchedConnectorId))
            .Where(r => r.MatchedConnectorId.HasValue)
            .ToList();
        totalDeps = requiredForLevel.Count;

        if (totalDeps > 0)
        {
            var minutesPerDep = Math.Max(0.1, workspaceOptions.Value.PushWaitDependencyTimeoutMinutesPerDependency);
            var timeoutMinutes = totalDeps * minutesPerDep;
            var totalTimeout = TimeSpan.FromMinutes(timeoutMinutes);
            using var timeoutCts = new CancellationTokenSource(totalTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var linkedToken = linkedCts.Token;
            var deadline = DateTime.UtcNow + totalTimeout;
            var foundByIndex = new bool[totalDeps];
            var observations = new PackageWaitObservation[totalDeps];
            for (var observationIndex = 0; observationIndex < totalDeps; observationIndex++)
                observations[observationIndex] = new PackageWaitObservation();
            var foundLock = new object();
            var ciRunWatch = ciProvider.CreatePushRunWatch(_overlayCommandTerminalService);
            int getFoundCount()
            {
                lock (foundLock) { return foundByIndex.Count(x => x); }
            }
            var lastPollUtc = DateTime.MinValue;
            var waitStartedUtc = DateTime.UtcNow;

            // Prefetch all connectors for this level once to avoid concurrent DbContext reads in the polling loop
            var connectorByIdForLevel = new Dictionary<int, Connector?>();
            foreach (var cid in requiredForLevel.Select(r => r.MatchedConnectorId!.Value).Distinct())
                connectorByIdForLevel[cid] = await _connectorRepository!.GetByIdAsync(cid);

            // One read of producer/consumer metadata. The polling loop does not touch the graph again.
            var producers = await LoadPackageProducersAsync(run, cancellationToken);
            LogPackageWaitStarted(run, level, totalLevels, reposAtLevel.Count, totalDeps, totalTimeout, connectorByIdForLevel);
            LogPackageExpectations(runId, level, requiredForLevel, producers);

            async Task AbortPackageWaitTimeout()
            {
                var found = getFoundCount();
                var waited = DateTime.UtcNow - waitStartedUtc;
                string[] missing;
                lock (foundLock)
                {
                    missing = Enumerable.Range(0, totalDeps)
                        .Where(i => !foundByIndex[i])
                        .Select(i => $"{requiredForLevel[i].PackageId} {requiredForLevel[i].Version}")
                        .ToArray();
                }

                _logger.LogWarning("[PushOrchestrator {RunId}] Push wait: timed out after {TotalMinutes:F1} min. Found {Found} of {Total}.", runId, waited.TotalMinutes, found, totalDeps);
                PackageWaitTimeout.Report(level, onLevelError, found, totalDeps, waited, missing);
                run.Diagnostics.FailedLevel = level;
                run.Diagnostics.FailureCategory = PackageWaitDiagnostics.FailurePackageWaitTimeout;
                run.Diagnostics.BlockedRepositories.AddRange(reposAtLevel.Select(repo => repo.RepoName));
                try
                {
                    await LogPackageWaitTimeoutAsync(
                        run,
                        level,
                        totalLevels,
                        reposAtLevel,
                        requiredForLevel,
                        observations,
                        producers,
                        connectorByIdForLevel,
                        foundByIndex,
                        waitStartedUtc,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PushOrchestrator {RunId}] Package wait diagnostics failed. WorkspaceId={WorkspaceId} Level={Level}", runId, workspaceId, level);
                }

                LogLevelOutcome(false, pushed: 0, failed: 0, blocked: reposAtLevel.Count, PackageWaitDiagnostics.FailurePackageWaitTimeout);
            }

            try
            {
                while (getFoundCount() < totalDeps)
                {
                if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    await AbortPackageWaitTimeout();
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    await AbortPackageWaitTimeout();
                    return false;
                }
                var found = getFoundCount();
                var line1 = found == 0
                    ? $"Waiting for {totalDeps} {(totalDeps == 1 ? "package" : "packages")}..."
                    : $"Found {found} of {totalDeps} {(totalDeps == 1 ? "package" : "packages")}";
                var totalSec = (int)remaining.TotalSeconds;
                var mm = totalSec / 60;
                var ss = totalSec % 60;
                levelProgress?.Invoke($"{line1}\n{mm:D2}:{ss:D2}");

                await ciRunWatch.TickAsync(pushedRepos, links, linkedToken);

                if ((DateTime.UtcNow - lastPollUtc).TotalSeconds >= PackageWaitDiagnostics.PollIntervalSeconds)
                {
                    lastPollUtc = DateTime.UtcNow;
                    int[] toCheck;
                    lock (foundLock)
                    {
                        toCheck = Enumerable.Range(0, totalDeps).Where(i => !foundByIndex[i]).ToArray();
                    }
                    if (toCheck.Length > 0)
                    {
                        var prevFound = getFoundCount();
                        foreach (var chunk in toCheck.Chunk(_maxConcurrent))
                        {
                            await Task.WhenAll(chunk.Select(async i =>
                            {
                                var req = requiredForLevel[i];
                                connectorByIdForLevel.TryGetValue(req.MatchedConnectorId!.Value, out var connector);
                                if (connector == null)
                                {
                                    if (observations[i].Observe(PackageVersionLookupKind.Inconclusive, null, false, DateTime.UtcNow))
                                    {
                                        PackageWaitDiagnostics.LogLookupTransition(
                                            _logger, runId, req.PackageId, req.Version, req.MatchedConnectorId, null, PackageVersionLookupKind.Inconclusive, null);
                                    }
                                    return;
                                }

                                PackageVersionLookupResult lookup;
                                try
                                {
                                    lookup = await _nuGetService!.LookupPackageVersionAsync(connector, req.PackageId, req.Version, linkedToken);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    var kind = ex is TimeoutException
                                        ? PackageVersionLookupKind.RequestTimeout
                                        : PackageVersionLookupKind.RegistryFailure;
                                    if (observations[i].Observe(kind, null, false, DateTime.UtcNow))
                                    {
                                        PackageWaitDiagnostics.LogLookupTransition(
                                            _logger, runId, req.PackageId, req.Version, connector.ConnectorId, connector.ConnectorName, kind, null);
                                    }
                                    throw;
                                }

                                var successfulQuery = lookup.StatusCode is >= 200 and <= 299;
                                if (observations[i].Observe(lookup.Kind, lookup.StatusCode, successfulQuery, DateTime.UtcNow))
                                {
                                    PackageWaitDiagnostics.LogLookupTransition(
                                        _logger, runId, req.PackageId, req.Version, connector.ConnectorId, connector.ConnectorName, lookup.Kind, lookup.StatusCode);
                                }
                                if (lookup.Exists)
                                {
                                    lock (foundLock)
                                        foundByIndex[i] = true;
                                }
                            }));
                        }
                        var nowFound = getFoundCount();
                        if (nowFound > prevFound)
                        {
                            _logger.LogInformation(
                                "[PushOrchestrator {RunId}] Package readiness changed. WorkspaceId={WorkspaceId} Level={Level} Found={Found} Missing={Missing} RequiredPackages={RequiredPackages}",
                                runId, workspaceId, level, nowFound, totalDeps - nowFound, totalDeps);
                            var stillWaiting = totalDeps - nowFound;
                            if (stillWaiting > 0)
                            {
                                // Each package found gives the rest a fresh budget, but never less time than is
                                // already left: the package still missing is often the one this run has just pushed,
                                // which is the slowest to appear, and a shorter deadline would stop the run early.
                                var proposed = DateTime.UtcNow + TimeSpan.FromMinutes(stillWaiting * minutesPerDep);
                                if (proposed > deadline)
                                {
                                    deadline = proposed;
                                    timeoutCts.CancelAfter(deadline - DateTime.UtcNow);
                                }
                            }
                        }
                        if (nowFound >= totalDeps)
                            _logger.LogInformation("[PushOrchestrator {RunId}] Push wait: all {Total} package(s) found for level {Level}, proceeding.", runId, totalDeps, level);
                    }
                }

                if (getFoundCount() >= totalDeps)
                    break;
                await Task.Delay(TimeSpan.FromSeconds(1), linkedToken);
            }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await AbortPackageWaitTimeout();
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[PushOrchestrator {RunId}] Push wait: level {Level} failed.", runId, level);
                onLevelError?.Invoke(level, ex.Message);
                LogLevelOutcome(false, pushed: 0, failed: 0, blocked: reposAtLevel.Count, PackageWaitDiagnostics.FailurePackageWaitFailed);
                return false;
            }
        }

        levelProgress?.Invoke($"Pushing {reposAtLevel.Count} {(reposAtLevel.Count == 1 ? "repository" : "repositories")}...");
        IReadOnlyList<(int RepoId, string Error)> levelFailures;
        try
        {
            levelFailures = await PushReposAsync(
                workspace,
                contextId,
                reposAtLevel,
                bearerByRepoId,
                levelProgress,
                onRepoError,
                onAppSideComplete: isLastLevel ? null : run.OnAppSideComplete,
                refreshVersionAfterPush: true,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: push failed.", runId, workspaceId, level);
            onLevelError?.Invoke(level, ex.Message);
            LogLevelOutcome(false, pushed: 0, failed: reposAtLevel.Count, blocked: 0, PackageWaitDiagnostics.FailureRepositoryPushFailed);
            return false;
        }
        await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, contextId, reposAtLevel, cancellationToken);
        if (levelFailures.Count > 0)
        {
            _logger.LogWarning(
                "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: aborting synchronized push after {FailedCount} repository failure(s).",
                runId, workspaceId, level, levelFailures.Count);
            LogLevelOutcome(false, reposAtLevel.Count - levelFailures.Count, levelFailures.Count, blocked: 0, PackageWaitDiagnostics.FailureRepositoryPushFailed);
            return false;
        }

        if (restorePackages)
        {
            levelProgress?.Invoke("Restoring packages...");
            try
            {
                var restoreFailed = run.RestoreOnlySyncedRepos || syncedRepoIds is { Count: > 0 }
                    ? await RestoreUpdatedReposAtLevelAsync(workspaceId, workspace.Name, workspaceRoot, workspaceRepositoryName, reposAtLevel, syncedRepoIds ?? new HashSet<int>(), onRepoError, cancellationToken)
                    : await TryRestoreReposAtLevelAsync(workspaceId, workspace.Name, workspaceRoot, workspaceRepositoryName, reposAtLevel, onRepoError, cancellationToken);
                if (restoreFailed)
                {
                    LogLevelOutcome(false, reposAtLevel.Count, failed: 0, blocked: 0, PackageWaitDiagnostics.FailureRestoreFailed);
                    return false;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: restore failed.", runId, workspaceId, level);
                onLevelError?.Invoke(level, ex.Message);
                LogLevelOutcome(false, reposAtLevel.Count, failed: 0, blocked: 0, PackageWaitDiagnostics.FailureRestoreFailed);
                return false;
            }
        }

        pushedRepos.AddRange(reposAtLevel);
        LogLevelOutcome(true, reposAtLevel.Count, failed: 0, blocked: 0, category: null);

        return true;
    }

    /// <summary>
    /// Whether <see cref="RunPushLaneAsync"/> can run alongside an update for this context: the registries must be
    /// reachable and every package a repository (up to <paramref name="maxLevel"/>) requires must already be matched
    /// to a registry. Syncs the registries first, exactly as the sequential push does before it checks the same
    /// mappings. Matching is by package id only, so the answer does not change when the update bumps versions.
    /// When false nothing has been changed, and the caller should run the sequential update-then-push instead.
    /// </summary>
    public async Task<bool> CanRunPushLaneAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int? maxLevel,
        CancellationToken cancellationToken)
    {
        if (_nuGetService == null || _connectorRepository == null)
            return false;

        async Task<IReadOnlyList<PushRepoPayload>> LoadPayloadAsync()
        {
            var all = await _workspaceDependencyService.GetPushPlanPayloadAsync(workspaceId, contextId.Value, cancellationToken);
            return maxLevel.HasValue ? all.Where(p => (p.DependencyLevel ?? 0) <= maxLevel.Value).ToList() : all;
        }

        var payload = await LoadPayloadAsync();
        var requiredPackageIds = payload
            .SelectMany(p => p.RequiredPackages)
            .Select(r => r.PackageId?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requiredPackageIds.Count > 0 && _packageRegistrySyncService != null)
        {
            await _packageRegistrySyncService.SyncRegistriesForPackageIdsAsync(workspaceId, requiredPackageIds, cancellationToken);
            payload = await LoadPayloadAsync();
        }

        return payload.All(p => p.RequiredPackages.All(r => r.MatchedConnectorId.HasValue));
    }

    /// <summary>
    /// The push half of a pipelined update-and-push. Reads each dependency level the update finishes from
    /// <paramref name="completedLevels"/> and pushes it as soon as it arrives, so a level's packages are building
    /// while the update is still committing higher levels. Per level it re-reads the push plan (a level's required
    /// package versions are only final once the update has committed it), pushes the repositories of that level that
    /// have unpushed commits or no upstream, waits for the packages the next levels need, and restores the repositories
    /// the update rewrote. Reports failures through the callbacks and then stops pushing, never throwing for them;
    /// levels the update has not finished (or never will, after a failure) are simply never pushed.
    /// Returns the number of repositories pushed.
    /// </summary>
    public async Task<int> RunPushLaneAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        ChannelReader<DependencyLevelCompletion> completedLevels,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        Action<int, string>? onLevelError = null,
        bool restorePackages = true,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        runId = GrayMoon.Abstractions.Logging.GrayMoonOperationContext.EnsureId(runId);
        if (!_workerBridge.IsWorkerConnected)
            throw new InvalidOperationException("Worker not connected. Start the GrayMoon Worker to push.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId)
            ?? throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var workerArgs = await ResolveWorkerPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);
        var configuredRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        await _workspaceService.CreateDirectoryAsync(workspace.Name, configuredRoot, cancellationToken);

        var links = await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .ThenInclude(r => r!.Connector)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        var bearerByRepoId = links
            .Where(wr => wr.Repository != null)
            .ToDictionary(
                wr => wr.RepositoryId,
                wr => ConnectorHelpers.UnprotectToken(wr.Repository!.Connector?.UserToken));
        var tagPinnedRepoIds = links
            .Where(wr => !string.IsNullOrWhiteSpace(wr.CheckedOutTag))
            .Select(wr => wr.RepositoryId)
            .ToHashSet();
        var ciProvider = _ciProviderResolver == null
            ? NoCiProvider.Instance
            : await _ciProviderResolver.GetForWorkspaceAsync(workspaceId, cancellationToken);

        // Grows as the update reports levels; restore only touches repositories whose csproj the update rewrote.
        var syncedRepoIds = new HashSet<int>();
        var run = new SynchronizedPushRun(
            workspace,
            contextId,
            workerArgs.WorkspaceRoot,
            workerArgs.WorkspaceRepositoryName,
            links,
            bearerByRepoId,
            ciProvider,
            restorePackages,
            syncedRepoIds,
            RestoreOnlySyncedRepos: true,
            runId,
            onProgressMessage,
            onRepoError,
            onLevelError,
            OnAppSideComplete: null)
        {
            FeatureLabel = await ResolveFeatureLabelAsync(workspaceId, contextId, cancellationToken)
        };

        _logger.LogInformation("[PushOrchestrator {RunId}] Workspace {WorkspaceId}: push lane starting.", runId, workspaceId);
        await foreach (var done in completedLevels.ReadAllAsync(cancellationToken))
        {
            syncedRepoIds.UnionWith(done.SyncedRepoIds);

            // Every level reads and writes through a DbContext of its own. The update lane keeps rewriting the higher
            // levels' rows while this lane pushes, and a context kept from an earlier level hands back the entities it
            // tracked then: an old GitVersion in the dependency recompute, and commit counts that never save because the
            // stale value already equals the new one.
            await using var levelScope = _scopeFactory.CreateAsyncScope();
            var levelPushService = levelScope.ServiceProvider.GetRequiredService<WorkspacePushService>();
            if (!await levelPushService.PushLaneLevelAsync(run, done, tagPinnedRepoIds, cancellationToken))
            {
                LogOperationSummary(run, pushSucceeded: false);
                return run.PushedRepos.Count;
            }
        }

        if (run.PushedRepos.Count > 0)
        {
            await using var finalScope = _scopeFactory.CreateAsyncScope();
            await finalScope.ServiceProvider.GetRequiredService<WorkspacePushService>()
                .UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, contextId, run.PushedRepos, cancellationToken);
        }

        _logger.LogInformation(
            "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: push lane finished. TotalPushed={TotalPushed}",
            runId, workspaceId, run.PushedRepos.Count);
        LogOperationSummary(run, pushSucceeded: true);
        return run.PushedRepos.Count;
    }

    /// <summary>
    /// One push lane hand-off, run on a <see cref="WorkspacePushService"/> of the level's own scope: re-reads the push
    /// plan, picks the level's repositories with something to push and pushes them. Returns false when the lane must stop
    /// (already reported through the run's callbacks).
    /// </summary>
    private async Task<bool> PushLaneLevelAsync(
        SynchronizedPushRun run,
        DependencyLevelCompletion done,
        IReadOnlySet<int> tagPinnedRepoIds,
        CancellationToken cancellationToken)
    {
        var workspaceId = run.Workspace.WorkspaceId;
        var contextId = run.ContextId;
        var runId = run.RunId;

        var payload = await _workspaceDependencyService.GetPushPlanPayloadAsync(workspaceId, contextId.Value, cancellationToken);
        run.Diagnostics.RememberPlan(payload);
        var needingPush = await _workspaceRepository.GetRepositoryIdsNeedingPushAsync(workspaceId, contextId.Value, done.RepoIds, cancellationToken);
        var reposAtLevel = payload
            .Where(p => done.RepoIds.Contains(p.RepoId)
                && !tagPinnedRepoIds.Contains(p.RepoId)
                && (needingPush.Contains(p.RepoId) || done.CommittedRepoIds.Contains(p.RepoId)))
            .ToList();
        var notSelected = done.RepoIds.Where(id => reposAtLevel.All(repo => repo.RepoId != id)).OrderBy(id => id).ToList();
        var consideredComplete = done.RepoIds.Where(id => !needingPush.Contains(id)).OrderBy(id => id).ToList();
        _logger.LogInformation(
            "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: push lane received Level {Level}: {PushCount} of {RepoCount} repo(s) to push: [{RepoIds}]",
            runId, workspaceId, done.Level, reposAtLevel.Count, done.RepoIds.Count, string.Join(",", reposAtLevel.Select(r => r.RepoId)));
        _logger.LogInformation(
            "[PushOrchestrator {RunId}] Push invocation state. WorkspaceId={WorkspaceId} Feature={Feature} Level={Level} Algorithm={Algorithm} NeedingPush=[{NeedingPush}] CommittedThisUpdate=[{CommittedThisUpdate}] Selected=[{Selected}] NotSelected=[{NotSelected}] ConsideredCompleteByNeedingPushCheck=[{ConsideredComplete}]",
            runId,
            workspaceId,
            run.FeatureLabel,
            done.Level,
            "PushNeedingOrCommittedThisUpdate",
            FormatIds(needingPush.OrderBy(id => id)),
            FormatIds(done.CommittedRepoIds.OrderBy(id => id)),
            FormatIds(reposAtLevel.Select(repo => repo.RepoId).OrderBy(id => id)),
            FormatIds(notSelected),
            FormatIds(consideredComplete));
        if (reposAtLevel.Count == 0)
            return true;

        var unmatchedPackages = reposAtLevel
            .SelectMany(p => p.RequiredPackages)
            .Where(r => !r.MatchedConnectorId.HasValue)
            .DistinctBy(r => (r.PackageId, r.Version))
            .Count();
        if (unmatchedPackages > 0)
        {
            _logger.LogWarning("[PushOrchestrator {RunId}] Push lane: Level {Level} has {Count} required package mapping(s) with no registry.", runId, done.Level, unmatchedPackages);
            run.OnLevelError?.Invoke(done.Level, $"Synchronized push could not continue: {unmatchedPackages} required package mapping(s) have no registry. Check the NuGet connector configuration and token.");
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var totalLevels = run.Diagnostics.PlannedLevels.Count > 0 ? run.Diagnostics.PlannedLevels.Count : 1;
        return await PushLevelAsync(run, done.Level, reposAtLevel, isLastLevel: false, totalLevels, done.RepoIds.Count - reposAtLevel.Count, cancellationToken);
    }

    /// <summary>Pushed a single repository's current branch with upstream (-u). Used when the user clicks the "not-upstreamed" badge.</summary>
    public async Task<(bool Success, string? ErrorMessage)> PushSingleRepositoryWithUpstreamAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? branchName,
        Action<string>? onProgressMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (!_workerBridge.IsWorkerConnected)
            return (false, "Worker not connected. Start the GrayMoon Worker to push.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return (false, "Workspace not found.");

        var link = await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .ThenInclude(r => r!.Connector)
            .FirstOrDefaultAsync(wr => wr.WorkspaceId == workspaceId && wr.RepositoryId == repositoryId, cancellationToken);

        if (link?.Repository == null)
            return (false, "Repository not in workspace or not found.");

        if (!string.IsNullOrWhiteSpace(link.CheckedOutTag))
            return (false, "Repository is pinned to a tag. Checkout a branch before pushing.");

        var repo = link.Repository;
        var workerArgs = await ResolveWorkerPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);
        var workspaceRoot = workerArgs.WorkspaceRoot;
        var workspaceFolderName = workerArgs.WorkspaceFolderName;
        var workspaceRepositoryName = workerArgs.WorkspaceRepositoryName;

        onProgressMessage?.Invoke(link.BranchHasUpstream == true ? "Pushing..." : "Pushing upstream...");

        if (_connectorHealthService != null)
            await _connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, cancellationToken);

        var capabilities = await ResolveRepositoryOperationCapabilitiesAsync(workspaceId, cancellationToken);
        var args = new
        {
            workspaceName = workspaceFolderName,
            repositoryId = repo.RepositoryId,
            repositoryName = repo.RepositoryName,
            bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
            workspaceId,
            workspaceRoot,
            workspaceRepositoryName,
            branchName = string.IsNullOrWhiteSpace(branchName) ? null : branchName.Trim(),
            capabilities
        };

        var response = await _workerBridge.SendCommandAsync("PushRepository", args, cancellationToken);
        var success = response.Success && response.Data != null && WorkerResponseJson.DeserializeWorkerResponse<PushRepositoryResponse>(response.Data) is { Success: true };
        if (!success)
        {
            var rawErr = response.Error ?? WorkerResponseJson.DeserializeWorkerResponse<PushRepositoryResponse>(response.Data!)?.ErrorMessage;
            if (PushErrorFormatter.IsNonFastForwardRejection(rawErr))
                await FetchAfterRejectionsAsync(workspaceId, contextId, [(repositoryId, repo.RepositoryName)], workspace.Name, workspaceRoot, workspaceRepositoryName, cancellationToken);
            return (false, PushErrorFormatter.Format(rawErr));
        }

        await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, contextId,
            [new PushRepoPayload(repositoryId, repo.RepositoryName, link.DependencyLevel, [])],
            cancellationToken);
        return (true, null);
    }

    /// <summary>Pushes a set of repos in dependency level order (lowest first) with upstream, without waiting for packages in registry.</summary>
    public async Task RunPushReposInLevelOrderAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repoIds,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        Action<int, string>? onLevelError = null,
        CancellationToken cancellationToken = default)
    {
        if (!_workerBridge.IsWorkerConnected)
            throw new InvalidOperationException("Worker not connected. Start the GrayMoon Worker to push.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var fullPayload = await _workspaceDependencyService.GetPushPlanPayloadAsync(workspaceId, contextId.Value, cancellationToken);
        var payload = fullPayload.Where(p => repoIds.Contains(p.RepoId)).ToList();
        if (payload.Count == 0)
        {
            onProgressMessage?.Invoke("No repositories to push.");
            return;
        }

        var links = await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .ThenInclude(r => r!.Connector)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        var bearerByRepoId = links
            .Where(wr => wr.Repository != null)
            .ToDictionary(
                wr => wr.RepositoryId,
                wr => ConnectorHelpers.UnprotectToken(wr.Repository!.Connector?.UserToken));

        var tagPinnedRepoIdsInLevelOrder = links
            .Where(wr => !string.IsNullOrWhiteSpace(wr.CheckedOutTag))
            .Select(wr => wr.RepositoryId)
            .ToHashSet();
        if (tagPinnedRepoIdsInLevelOrder.Count > 0)
            payload = payload.Where(p => !tagPinnedRepoIdsInLevelOrder.Contains(p.RepoId)).ToList();
        if (payload.Count == 0)
        {
            onProgressMessage?.Invoke("No repositories to push.");
            return;
        }

        var levelsAsc = payload.Select(p => p.DependencyLevel ?? 0).Distinct().OrderBy(x => x).ToList();
        foreach (var level in levelsAsc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reposAtLevel = payload.Where(p => (p.DependencyLevel ?? 0) == level).ToList();
            if (reposAtLevel.Count == 0) continue;
            var levelProgress = onProgressMessage == null ? (Action<string>?)null : msg => onProgressMessage($"{msg}\nLevel {level}");
            levelProgress?.Invoke($"Pushing {reposAtLevel.Count} {(reposAtLevel.Count == 1 ? "repository" : "repositories")}...");
            await PushReposAsync(workspace, contextId, reposAtLevel, bearerByRepoId, levelProgress, onRepoError, onAppSideComplete: null, cancellationToken: cancellationToken);
            await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, contextId, reposAtLevel, cancellationToken);
        }

        await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, contextId, payload, cancellationToken);
        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
    }

    /// <summary>Pushes a set of repos in parallel (up to MaxParallelOperations concurrency), without dependency ordering or waiting for packages.</summary>
    public async Task RunPushReposParallelAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> repoIds,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        Action<int, string>? onLevelError = null,
        Action? onAppSideComplete = null,
        CancellationToken cancellationToken = default)
    {
        if (!_workerBridge.IsWorkerConnected)
            throw new InvalidOperationException("Worker not connected. Start the GrayMoon Worker to push.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var fullPayload = await _workspaceDependencyService.GetPushPlanPayloadAsync(workspaceId, contextId.Value, cancellationToken);
        await RunPushReposParallelAsync(workspace, contextId, fullPayload, repoIds, onProgressMessage, onRepoError, onAppSideComplete, cancellationToken);
    }

    /// <summary>
    /// Pushes the <paramref name="repoIds"/> subset of an already-built <paramref name="fullPayload"/> in parallel, without
    /// dependency ordering or waiting for packages. Lets a caller that built the payload without dependency data push it
    /// without this service reading any.
    /// </summary>
    public async Task RunPushReposParallelAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<PushRepoPayload> fullPayload,
        IReadOnlySet<int> repoIds,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        Action? onAppSideComplete = null,
        CancellationToken cancellationToken = default)
    {
        if (!_workerBridge.IsWorkerConnected)
            throw new InvalidOperationException("Worker not connected. Start the GrayMoon Worker to push.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        await RunPushReposParallelAsync(workspace, contextId, fullPayload, repoIds, onProgressMessage, onRepoError, onAppSideComplete, cancellationToken);
    }

    private async Task RunPushReposParallelAsync(
        Workspace workspace,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<PushRepoPayload> fullPayload,
        IReadOnlySet<int> repoIds,
        Action<string>? onProgressMessage,
        Action<int, string>? onRepoError,
        Action? onAppSideComplete,
        CancellationToken cancellationToken)
    {
        var workspaceId = workspace.WorkspaceId;
        IReadOnlyList<PushRepoPayload> payload = fullPayload.Where(p => repoIds.Contains(p.RepoId)).ToList();
        if (payload.Count == 0)
        {
            onProgressMessage?.Invoke("No repositories to push.");
            return;
        }

        var links = await _dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Include(wr => wr.Repository)
            .ThenInclude(r => r!.Connector)
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        var bearerByRepoId = links
            .Where(wr => wr.Repository != null)
            .ToDictionary(
                wr => wr.RepositoryId,
                wr => ConnectorHelpers.UnprotectToken(wr.Repository!.Connector?.UserToken));

        var tagPinnedRepoIdsParallel = links
            .Where(wr => !string.IsNullOrWhiteSpace(wr.CheckedOutTag))
            .Select(wr => wr.RepositoryId)
            .ToHashSet();
        if (tagPinnedRepoIdsParallel.Count > 0)
            payload = payload.Where(p => !tagPinnedRepoIdsParallel.Contains(p.RepoId)).ToList();
        if (payload.Count == 0)
        {
            onProgressMessage?.Invoke("No repositories to push.");
            return;
        }

        onProgressMessage?.Invoke($"Pushing {payload.Count} {(payload.Count == 1 ? "repository" : "repositories")}...");
        await PushReposAsync(workspace, contextId, payload, bearerByRepoId, onProgressMessage, onRepoError, onAppSideComplete, cancellationToken: cancellationToken);
        await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, contextId, payload, cancellationToken);
    }

    private async Task<bool> RestoreUpdatedReposAtLevelAsync(
        int workspaceId,
        string workspaceName,
        string? workspaceRoot,
        string? workspaceRepositoryName,
        IReadOnlyList<PushRepoPayload> repos,
        IReadOnlySet<int> syncedRepoIds,
        Action<int, string>? onRepoError,
        CancellationToken cancellationToken)
    {
        var repoIdsToRestore = repos
            .Select(r => r.RepoId)
            .Where(id => syncedRepoIds.Contains(id))
            .ToHashSet();

        if (repoIdsToRestore.Count == 0) return false;

        var repoNameById = repos
            .Where(r => repoIdsToRestore.Contains(r.RepoId))
            .ToDictionary(r => r.RepoId, r => r.RepoName);

        var projects = await _dbContext.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && repoIdsToRestore.Contains(p.RepositoryId) && p.ProjectFilePath != null)
            .ToListAsync(cancellationToken);

        if (projects.Count == 0) return false;

        var pathsByRepoId = projects
            .GroupBy(p => p.RepositoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.ProjectFilePath!).ToList());

        var failed = new int[1];
        var tasks = pathsByRepoId.Select(async kvp =>
        {
            if (!repoNameById.TryGetValue(kvp.Key, out var repositoryName)) return;
            try
            {
                await _workerBridge.SendCommandAsync(
                    "DotnetRestore",
                    new { workspaceName, repositoryName, projectPaths = kvp.Value, workspaceRoot, workspaceRepositoryName },
                    cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "dotnet restore failed for {RepoName} in workspace {WorkspaceName}", repositoryName, workspaceName);
                onRepoError?.Invoke(kvp.Key, ex.Message);
                Interlocked.Exchange(ref failed[0], 1);
            }
        });
        await Task.WhenAll(tasks);
        return failed[0] != 0;
    }

    private async Task<bool> TryRestoreReposAtLevelAsync(
        int workspaceId,
        string workspaceName,
        string? workspaceRoot,
        string? workspaceRepositoryName,
        IReadOnlyList<PushRepoPayload> repos,
        Action<int, string>? onRepoError,
        CancellationToken cancellationToken)
    {
        var repoIdSet = repos.Select(r => r.RepoId).ToHashSet();
        var repoNameById = repos.ToDictionary(r => r.RepoId, r => r.RepoName);

        var projects = await _dbContext.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && repoIdSet.Contains(p.RepositoryId))
            .ToListAsync(cancellationToken);

        if (projects.Count == 0) return false;

        var projectIdToRepoId = projects.ToDictionary(p => p.ProjectId, p => p.RepositoryId);
        var projectIdSet = projectIdToRepoId.Keys.ToHashSet();

        var deps = await _dbContext.ProjectDependencies
            .AsNoTracking()
            .Where(d => projectIdSet.Contains(d.DependentProjectId))
            .ToListAsync(cancellationToken);

        var projectFilePathById = projects.ToDictionary(p => p.ProjectId, p => p.ProjectFilePath);

        var pathsByRepoId = new Dictionary<int, List<string>>();
        foreach (var dep in deps)
        {
            var depRepoId = projectIdToRepoId.GetValueOrDefault(dep.DependentProjectId, -1);
            var refRepoId = projectIdToRepoId.GetValueOrDefault(dep.ReferencedProjectId, -1);
            if (depRepoId < 0 || refRepoId < 0 || depRepoId == refRepoId) continue;
            if (!projectFilePathById.TryGetValue(dep.DependentProjectId, out var filePath) || string.IsNullOrWhiteSpace(filePath)) continue;
            if (!pathsByRepoId.TryGetValue(depRepoId, out var list))
                pathsByRepoId[depRepoId] = list = [];
            if (!list.Contains(filePath))
                list.Add(filePath);
        }

        if (pathsByRepoId.Count == 0) return false;

        var failed = new int[1];
        var tasks = pathsByRepoId.Select(async kvp =>
        {
            if (!repoNameById.TryGetValue(kvp.Key, out var repositoryName)) return;
            try
            {
                await _workerBridge.SendCommandAsync(
                    "DotnetRestore",
                    new { workspaceName, repositoryName, projectPaths = (IReadOnlyList<string>)kvp.Value, workspaceRoot, workspaceRepositoryName },
                    cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "dotnet restore failed for {RepoName} in workspace {WorkspaceName}", repositoryName, workspaceName);
                onRepoError?.Invoke(kvp.Key, ex.Message);
                Interlocked.Exchange(ref failed[0], 1);
            }
        });
        await Task.WhenAll(tasks);
        return failed[0] != 0;
    }

    private async Task<IReadOnlyList<(int RepoId, string Error)>> PushReposAsync(
        Workspace workspace,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<PushRepoPayload> repos,
        IReadOnlyDictionary<int, string?> bearerByRepoId,
        Action<string>? onProgressMessage,
        Action<int, string>? onRepoError,
        Action? onAppSideComplete = null,
        bool refreshVersionAfterPush = false,
        CancellationToken cancellationToken = default)
    {
        var succeeded = 0;
        var finished = 0;
        var total = repos.Count;
        using var semaphore = new SemaphoreSlim(_maxConcurrent);
        // The health check reads through the scoped AppDbContext, which must not be used by two pushes at once.
        using var healthCheckLock = new SemaphoreSlim(1, 1);
        var workerArgs = await ResolveWorkerPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);
        var workspaceRoot = workerArgs.WorkspaceRoot;
        var workspaceFolderName = workerArgs.WorkspaceFolderName;
        var workspaceRepositoryName = workerArgs.WorkspaceRepositoryName;
        var capabilities = await ResolveRepositoryOperationCapabilitiesAsync(workspace.WorkspaceId, cancellationToken);
        var rejectedRepos = new ConcurrentBag<(int RepoId, string RepoName)>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<(int RepoId, string Error)>();
        var pushTasks = repos.Select(async repo =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                try
                {
                    if (_connectorHealthService != null)
                    {
                        await healthCheckLock.WaitAsync(cancellationToken);
                        try
                        {
                            await _connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepoId, cancellationToken);
                        }
                        finally
                        {
                            healthCheckLock.Release();
                        }
                    }

                    var args = new
                    {
                        workspaceName = workspaceFolderName,
                        repositoryId = repo.RepoId,
                        repositoryName = repo.RepoName,
                        bearerToken = bearerByRepoId.GetValueOrDefault(repo.RepoId),
                        workspaceId = workspace.WorkspaceId,
                        workspaceRoot,
                        workspaceRepositoryName,
                        refreshVersionAfterPush,
                        capabilities
                    };
                    var response = await _workerBridge.SendCommandAsync("PushRepository", args, cancellationToken);
                    var success = response.Success && response.Data != null && WorkerResponseJson.DeserializeWorkerResponse<PushRepositoryResponse>(response.Data) is { Success: true };
                    if (!success)
                    {
                        var rawErr = response.Error ?? WorkerResponseJson.DeserializeWorkerResponse<PushRepositoryResponse>(response.Data!)?.ErrorMessage;
                        if (PushErrorFormatter.IsNonFastForwardRejection(rawErr))
                            rejectedRepos.Add((repo.RepoId, repo.RepoName));
                        var formatted = PushErrorFormatter.Format(rawErr);
                        failures.Add((repo.RepoId, formatted));
                        onRepoError?.Invoke(repo.RepoId, formatted);
                    }
                    else
                    {
                        var c = Interlocked.Increment(ref succeeded);
                        onProgressMessage?.Invoke($"Pushed {c} of {total}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Push failed for repository {RepositoryId} in workspace {WorkspaceId}", repo.RepoId, workspace.WorkspaceId);
                    failures.Add((repo.RepoId, ex.Message));
                    onRepoError?.Invoke(repo.RepoId, ex.Message);
                }

                if (Interlocked.Increment(ref finished) == total)
                    onAppSideComplete?.Invoke();
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(pushTasks);
        await FetchAfterRejectionsAsync(workspace.WorkspaceId, contextId, rejectedRepos.ToList(), workspace.Name, workspaceRoot, workspaceRepositoryName, cancellationToken);
        return failures.ToList();
    }

    private async Task<RepositoryOperationCapabilities> ResolveRepositoryOperationCapabilitiesAsync(int workspaceId, CancellationToken cancellationToken)
        => (await _capabilitiesResolver.GetAsync(workspaceId, cancellationToken)).ToRepositoryOperationCapabilities();

    internal async Task FetchAfterRejectionsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<(int RepoId, string RepoName)> rejectedRepos,
        string workspaceName,
        string? workspaceRoot,
        string? workspaceRepositoryName,
        CancellationToken cancellationToken)
    {
        if (rejectedRepos.Count == 0)
            return;

        // Phase 1: Worker RefreshBranches round trips fan out, bounded by _maxConcurrent.
        var refreshed = new ConcurrentBag<(int RepoId, string RepoName)>();
        using (var semaphore = new SemaphoreSlim(_maxConcurrent))
        {
            var tasks = rejectedRepos.Select(async repo =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    await _workerBridge.SendCommandAsync("RefreshBranches", new
                    {
                        workspaceName,
                        repositoryId = repo.RepoId,
                        repositoryName = repo.RepoName,
                        workspaceRoot,
                        workspaceRepositoryName
                    }, cancellationToken);
                    refreshed.Add(repo);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fetch after push rejection failed for repo {RepoId}", repo.RepoId);
                }
                finally
                {
                    semaphore.Release();
                }
            });
            await Task.WhenAll(tasks);
        }

        // Phase 2: DB updates share the scoped _dbContext, so they run strictly one at a time.
        var refreshedIds = refreshed.Select(r => r.RepoId).ToHashSet();
        foreach (var (repoId, repoName) in rejectedRepos.Where(r => refreshedIds.Contains(r.RepoId)))
        {
            try
            {
                await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, contextId,
                    [new PushRepoPayload(repoId, repoName, null, [])],
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fetch after push rejection failed for repo {RepoId}", repoId);
            }
        }
    }

    internal async Task UpdateCommitCountsAndUpstreamAfterPushAsync(int workspaceId, WorkspaceFeatureContextId contextId, IReadOnlyList<PushRepoPayload> repos, CancellationToken cancellationToken)
    {
        if (repos.Count == 0) return;
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return;

        var repoIds = repos.Select(r => r.RepoId).ToHashSet();
        var links = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId && repoIds.Contains(wr.RepositoryId))
            .ToListAsync(cancellationToken);

        // Persist the remote branch for each pushed repo so it appears in Remotes without calling refresh branches.
        var now = DateTime.UtcNow;
        var remoteCandidates = links
            .Where(wr => string.IsNullOrWhiteSpace(wr.CheckedOutTag) && !string.IsNullOrWhiteSpace(wr.BranchName))
            .Select(wr => (
                Link: wr,
                RemoteBranchName: wr.BranchName!.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
                    ? wr.BranchName!
                    : "origin/" + wr.BranchName))
            .ToList();
        if (remoteCandidates.Count > 0)
        {
            var wrlIds = remoteCandidates.Select(c => c.Link.WorkspaceRepositoryId).ToList();
            var existingRemotes = await _dbContext.RepositoryBranches
                .Where(rb => wrlIds.Contains(rb.WorkspaceRepositoryId) && rb.IsRemote)
                .Select(rb => new { rb.WorkspaceRepositoryId, rb.BranchName })
                .ToListAsync(cancellationToken);
            var existingRemoteSet = existingRemotes
                .Select(rb => (rb.WorkspaceRepositoryId, rb.BranchName))
                .ToHashSet();
            var toAdd = new List<RepositoryBranch>();
            foreach (var (link, remoteBranchName) in remoteCandidates)
            {
                if (!existingRemoteSet.Contains((link.WorkspaceRepositoryId, remoteBranchName)))
                {
                    toAdd.Add(new RepositoryBranch
                    {
                        WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                        BranchName = remoteBranchName,
                        IsRemote = true,
                        LastSeenAt = now,
                        IsDefault = false
                    });
                }

                link.BranchHasUpstream = true;
            }

            if (toAdd.Count > 0)
                _dbContext.RepositoryBranches.AddRange(toAdd);
        }

        var workerArgs = await ResolveWorkerPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);
        var workspaceRoot = workerArgs.WorkspaceRoot;
        var workspaceFolderName = workerArgs.WorkspaceFolderName;
        var workspaceRepositoryName = workerArgs.WorkspaceRepositoryName;

        var tagPinnedInLinks = links
            .Where(l => !string.IsNullOrWhiteSpace(l.CheckedOutTag))
            .Select(l => l.RepositoryId)
            .ToHashSet();
        var results = await Task.WhenAll(repos
            .Where(r => !tagPinnedInLinks.Contains(r.RepoId))
            .Select(async repo =>
        {
            try
            {
                var response = await _workerBridge.SendCommandAsync("GetCommitCounts", new
                {
                    workspaceName = workspaceFolderName,
                    repositoryName = repo.RepoName,
                    workspaceRoot,
                    workspaceRepositoryName
                }, cancellationToken);
                if (!response.Success || response.Data == null)
                    return (RepoId: repo.RepoId, Data: (WorkerCommitCountsResponse?)null);
                return (RepoId: repo.RepoId, Data: WorkerResponseJson.DeserializeWorkerResponse<WorkerCommitCountsResponse>(response.Data));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetCommitCounts failed for repo {RepoId} ({RepoName})", repo.RepoId, repo.RepoName);
                return (RepoId: repo.RepoId, Data: (WorkerCommitCountsResponse?)null);
            }
        }));

        // A failed or missing response leaves the persisted counts alone: nothing is marked probed, so the
        // writer has nothing to replace. Overwriting them with nulls would blank the badges after a
        // transient worker hiccup. Pending remote-branch rows flush with this one save.
        var snapshots = results
            .Where(r => r.Data != null)
            .Select(r => (r.RepoId, new RepositoryStateSnapshot
            {
                OutgoingCommits = r.Data!.OutgoingCommits,
                IncomingCommits = r.Data.IncomingCommits,
                DefaultBranchBehind = r.Data.DefaultBranchBehind,
                DefaultBranchAhead = r.Data.DefaultBranchAhead,
                HasUpstream = r.Data.HasUpstream,
                CommitCountsProbed = true,
                UpstreamProbed = r.Data.HasUpstream.HasValue,
            }))
            .ToList();
        if (snapshots.Count > 0)
        {
            await _stateWriter.ApplyBatchAsync(contextId, workspaceId, snapshots, cancellationToken: cancellationToken);
        }
        else
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await _recomputeScope.CompleteAsync(workspaceId, contextId, cancellationToken);
    }

    private Task<WorkerWorkspaceArgs> ResolveWorkerPathArgsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
        => _pathResolver.GetWorkerArgsAsync(contextId, cancellationToken);
}