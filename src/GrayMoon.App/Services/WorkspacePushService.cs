using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services;

/// <summary>
/// Push workflow implementation for a workspace.
/// This service exists to isolate push-specific logic from the much larger <see cref="WorkspaceGitService"/>.
/// Stateless (no UI state); caller owns CTS / progress / toast.
/// </summary>
public sealed class WorkspacePushService(
    IAgentBridge agentBridge,
    WorkspaceService workspaceService,
    WorkspaceRepository workspaceRepository,
    WorkspaceDependencyService workspaceDependencyService,
    WorkspaceProjectRepository workspaceProjectRepository,
    AppDbContext dbContext,
    Microsoft.Extensions.Options.IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<WorkspacePushService> logger,
    IHubContext<WorkspaceSyncHub>? hubContext = null,
    PackageRegistrySyncService? packageRegistrySyncService = null,
    NuGetService? nuGetService = null,
    ConnectorRepository? connectorRepository = null,
    ConnectorHealthService? connectorHealthService = null)
{
    private readonly IAgentBridge _agentBridge = agentBridge ?? throw new ArgumentNullException(nameof(agentBridge));
    private readonly WorkspaceService _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
    private readonly WorkspaceRepository _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
    private readonly WorkspaceDependencyService _workspaceDependencyService = workspaceDependencyService ?? throw new ArgumentNullException(nameof(workspaceDependencyService));
    private readonly WorkspaceProjectRepository _workspaceProjectRepository = workspaceProjectRepository ?? throw new ArgumentNullException(nameof(workspaceProjectRepository));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<WorkspacePushService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly int _maxConcurrent = Math.Max(1, workspaceOptions?.Value?.MaxParallelOperations ?? 16);
    private readonly IHubContext<WorkspaceSyncHub>? _hubContext = hubContext;
    private readonly PackageRegistrySyncService? _packageRegistrySyncService = packageRegistrySyncService;
    private readonly NuGetService? _nuGetService = nuGetService;
    private readonly ConnectorRepository? _connectorRepository = connectorRepository;
    private readonly ConnectorHealthService? _connectorHealthService = connectorHealthService;

    /// <summary>Gets the push plan: all workspace repos by dependency level. Used to show multi-level push dialog and push with dependency synchronization.</summary>
    public async Task<(IReadOnlyList<PushRepoPayload> Payload, bool IsMultiLevel)> GetPushPlanAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var payload = await _workspaceDependencyService.GetPushPlanPayloadAsync(workspaceId, cancellationToken);
        if (payload.Count == 0)
            return (payload, false);
        var levels = payload.Select(p => p.DependencyLevel ?? 0).Distinct().ToList();
        var isMultiLevel = levels.Count > 1;
        return (payload, isMultiLevel);
    }

    /// <summary>
    /// Runs dependency-synchronized push: sync package registries (unless already done by caller), then push by level (lowest first).
    /// For each level, waits until required packages are in registry (or pushes all at once if not possible), then pushes all repos at that level in parallel.
    /// Ensures branch is upstreamed even when there are no commits to push.
    /// When <paramref name="repoIdsToPush"/> is set, only those repos are pushed.
    /// Set <paramref name="packageRegistriesAlreadySynced"/> to true when the caller already synced required packages
    /// (e.g. via SyncRegistriesForPackageIdsAsync) to avoid syncing twice.
    /// </summary>
    public async Task RunPushAsync(
        int workspaceId,
        IReadOnlySet<int>? repoIdsToPush = null,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        Action? onAppSideComplete = null,
        bool packageRegistriesAlreadySynced = false,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected. Start GrayMoon.Agent to push.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        await _workspaceService.CreateDirectoryAsync(workspace.Name, workspaceRoot, cancellationToken);

        if (!packageRegistriesAlreadySynced)
        {
            onProgressMessage?.Invoke("Syncing package registries...");
            if (_packageRegistrySyncService != null)
                await _packageRegistrySyncService.SyncWorkspacePackageRegistriesAsync(workspaceId, cancellationToken: cancellationToken);
        }

        var fullPayload = await _workspaceDependencyService.GetPushPlanPayloadAsync(workspaceId, cancellationToken);
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
            await PushReposAsync(workspace, payload, bearerByRepoId, onProgressMessage, onRepoError, onAppSideComplete, cancellationToken);
            await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, payload, cancellationToken);
            if (_hubContext != null)
                await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
            return;
        }

        var levelsAsc = payload.Select(p => p.DependencyLevel ?? 0).Distinct().OrderBy(x => x).ToList();
        var lastLevel = levelsAsc[^1];
        foreach (var level in levelsAsc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reposAtLevel = payload.Where(p => (p.DependencyLevel ?? 0) == level).ToList();
            if (reposAtLevel.Count == 0) continue;

            var requiredForLevel = reposAtLevel
                .SelectMany(r => r.RequiredPackages)
                .DistinctBy(r => (r.PackageId, r.Version, r.MatchedConnectorId))
                .Where(r => r.MatchedConnectorId.HasValue)
                .ToList();
            var totalDeps = requiredForLevel.Count;

            if (totalDeps > 0)
            {
                _logger.LogInformation("Push wait: level {Level}, waiting for {Count} package(s): {Packages}",
                    level,
                    totalDeps,
                    string.Join(", ", requiredForLevel.Select(r => r.PackageId + "@" + r.Version + " (connector " + r.MatchedConnectorId + ")")));

                var timeoutMinutes = totalDeps * Math.Max(0.1, workspaceOptions.Value.PushWaitDependencyTimeoutMinutesPerDependency);
                var totalTimeout = TimeSpan.FromMinutes(timeoutMinutes);
                using var timeoutCts = new CancellationTokenSource(totalTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                var linkedToken = linkedCts.Token;
                var deadline = DateTime.UtcNow + totalTimeout;
                var foundByIndex = new bool[totalDeps];
                var foundLock = new object();
                int getFoundCount()
                {
                    lock (foundLock) { return foundByIndex.Count(x => x); }
                }
                var lastPollUtc = DateTime.MinValue;

                while (getFoundCount() < totalDeps)
                {
                    linkedToken.ThrowIfCancellationRequested();
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        onProgressMessage?.Invoke("Timed out.");
                        _logger.LogWarning("Push wait: timed out after {TotalMinutes:F1} min. Found {Found} of {Total}.", totalTimeout.TotalMinutes, getFoundCount(), totalDeps);
                        throw new OperationCanceledException("Push wait for dependencies timed out.");
                    }
                    var found = getFoundCount();
                    var line1 = found == 0
                        ? $"Waiting for {totalDeps} {(totalDeps == 1 ? "dependency" : "dependencies")}"
                        : $"Found {found} of {totalDeps} {(totalDeps == 1 ? "dependency" : "dependencies")}";
                    var totalSec = (int)remaining.TotalSeconds;
                    var mm = totalSec / 60;
                    var ss = totalSec % 60;
                    onProgressMessage?.Invoke($"{line1}\n{mm:D2}:{ss:D2}");

                    if ((DateTime.UtcNow - lastPollUtc).TotalSeconds >= 2)
                    {
                        lastPollUtc = DateTime.UtcNow;
                        int[] toCheck;
                        lock (foundLock)
                        {
                            toCheck = Enumerable.Range(0, totalDeps).Where(i => !foundByIndex[i]).ToArray();
                        }
                        if (toCheck.Length > 0)
                        {
                            foreach (var chunk in toCheck.Chunk(_maxConcurrent))
                            {
                                await Task.WhenAll(chunk.Select(async i =>
                                {
                                    var req = requiredForLevel[i];
                                    var connector = await _connectorRepository.GetByIdAsync(req.MatchedConnectorId!.Value);
                                    if (connector == null)
                                    {
                                        _logger.LogWarning("Push wait: package {PackageId} {Version} has no connector (MatchedConnectorId={ConnectorId}).", req.PackageId, req.Version, req.MatchedConnectorId);
                                        return;
                                    }
                                    var exists = await _nuGetService.PackageVersionExistsAsync(connector, req.PackageId, req.Version, linkedToken);
                                    _logger.LogInformation("Push wait: checking {PackageId} {Version} in registry {ConnectorName} (Id={ConnectorId}) -> {Result}",
                                        req.PackageId, req.Version, connector.ConnectorName, connector.ConnectorId, exists ? "found" : "not found");
                                    if (exists)
                                    {
                                        lock (foundLock)
                                            foundByIndex[i] = true;
                                    }
                                }));
                            }
                            if (getFoundCount() >= totalDeps)
                                _logger.LogInformation("Push wait: all {Total} package(s) found for level {Level}, proceeding.", totalDeps, level);
                        }
                    }

                    if (getFoundCount() >= totalDeps)
                        break;
                    await Task.Delay(TimeSpan.FromSeconds(1), linkedToken);
                }
            }

            onProgressMessage?.Invoke($"Pushing {reposAtLevel.Count} {(reposAtLevel.Count == 1 ? "repository" : "repositories")}...");
            await PushReposAsync(
                workspace,
                reposAtLevel,
                bearerByRepoId,
                onProgressMessage,
                onRepoError,
                onAppSideComplete: level == lastLevel ? null : onAppSideComplete,
                cancellationToken);
            await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, reposAtLevel, cancellationToken);
        }

        await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, payload, cancellationToken);
        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
    }

    /// <summary>Pushed a single repository's current branch with upstream (-u). Used when the user clicks the "not-upstreamed" badge.</summary>
    public async Task<(bool Success, string? ErrorMessage)> PushSingleRepositoryWithUpstreamAsync(
        int workspaceId,
        int repositoryId,
        string? branchName,
        Action<string>? onProgressMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            return (false, "Agent not connected. Start GrayMoon.Agent to push.");

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

        var repo = link.Repository;
        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);

        onProgressMessage?.Invoke(link.BranchHasUpstream == true ? "Pushing..." : "Pushing upstream...");

        if (_connectorHealthService != null)
            await _connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepositoryId, cancellationToken);

        var args = new
        {
            workspaceName = workspace.Name,
            repositoryId = repo.RepositoryId,
            repositoryName = repo.RepositoryName,
            bearerToken = ConnectorHelpers.UnprotectToken(repo.Connector?.UserToken),
            workspaceId,
            workspaceRoot,
            branchName = string.IsNullOrWhiteSpace(branchName) ? null : branchName.Trim()
        };

        var response = await _agentBridge.SendCommandAsync("PushRepository", args, cancellationToken);
        var success = response.Success && response.Data != null && AgentResponseJson.DeserializeAgentResponse<PushRepositoryResponse>(response.Data) is { Success: true };
        if (!success)
        {
            var err = response.Error ?? AgentResponseJson.DeserializeAgentResponse<PushRepositoryResponse>(response.Data!)?.ErrorMessage ?? "Push failed";
            return (false, err);
        }

        await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId,
            [new PushRepoPayload(repositoryId, repo.RepositoryName, link.DependencyLevel, [])],
            cancellationToken);
        return (true, null);
    }

    /// <summary>Pushes a set of repos in dependency level order (lowest first) with upstream, without waiting for packages in registry.</summary>
    public async Task RunPushReposInLevelOrderAsync(
        int workspaceId,
        IReadOnlySet<int> repoIds,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected. Start GrayMoon.Agent to push.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var fullPayload = await _workspaceDependencyService.GetPushPlanPayloadAsync(workspaceId, cancellationToken);
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

        var levelsAsc = payload.Select(p => p.DependencyLevel ?? 0).Distinct().OrderBy(x => x).ToList();
        foreach (var level in levelsAsc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reposAtLevel = payload.Where(p => (p.DependencyLevel ?? 0) == level).ToList();
            if (reposAtLevel.Count == 0) continue;
            onProgressMessage?.Invoke($"Pushing {reposAtLevel.Count} {(reposAtLevel.Count == 1 ? "repository" : "repositories")}...");
            await PushReposAsync(workspace, reposAtLevel, bearerByRepoId, onProgressMessage, onRepoError, onAppSideComplete: null, cancellationToken);
            await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, reposAtLevel, cancellationToken);
        }

        await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, payload, cancellationToken);
        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
    }

    /// <summary>Pushes a set of repos in parallel (up to MaxParallelOperations concurrency), without dependency ordering or waiting for packages.</summary>
    public async Task RunPushReposParallelAsync(
        int workspaceId,
        IReadOnlySet<int> repoIds,
        Action<string>? onProgressMessage = null,
        Action<int, string>? onRepoError = null,
        Action? onAppSideComplete = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new InvalidOperationException("Agent not connected. Start GrayMoon.Agent to push.");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var fullPayload = await _workspaceDependencyService.GetPushPlanPayloadAsync(workspaceId, cancellationToken);
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

        onProgressMessage?.Invoke($"Pushing {payload.Count} {(payload.Count == 1 ? "repository" : "repositories")}...");
        await PushReposAsync(workspace, payload, bearerByRepoId, onProgressMessage, onRepoError, onAppSideComplete, cancellationToken);
        await UpdateCommitCountsAndUpstreamAfterPushAsync(workspaceId, payload, cancellationToken);
        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
    }

    private async Task PushReposAsync(
        Workspace workspace,
        IReadOnlyList<PushRepoPayload> repos,
        IReadOnlyDictionary<int, string?> bearerByRepoId,
        Action<string>? onProgressMessage,
        Action<int, string>? onRepoError,
        Action? onAppSideComplete = null,
        CancellationToken cancellationToken = default)
    {
        var completed = 0;
        var total = repos.Count;
        using var semaphore = new SemaphoreSlim(_maxConcurrent);
        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var pushTasks = repos.Select(async repo =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                if (_connectorHealthService != null)
                    await _connectorHealthService.EnsureConnectorHealthyForRepositoryAsync(repo.RepoId, cancellationToken);

                var args = new
                {
                    workspaceName = workspace.Name,
                    repositoryId = repo.RepoId,
                    repositoryName = repo.RepoName,
                    bearerToken = bearerByRepoId.GetValueOrDefault(repo.RepoId),
                    workspaceId = workspace.WorkspaceId,
                    workspaceRoot
                };
                var response = await _agentBridge.SendCommandAsync("PushRepository", args, cancellationToken);
                var success = response.Success && response.Data != null && AgentResponseJson.DeserializeAgentResponse<PushRepositoryResponse>(response.Data) is { Success: true };
                if (!success)
                {
                    var err = response.Error ?? AgentResponseJson.DeserializeAgentResponse<PushRepositoryResponse>(response.Data!)?.ErrorMessage ?? "Push failed";
                    onRepoError?.Invoke(repo.RepoId, err);
                }
                var c = Interlocked.Increment(ref completed);
                onProgressMessage?.Invoke($"Pushed {c} of {total}");
                if (c == total)
                    onAppSideComplete?.Invoke();
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(pushTasks);
    }

    private async Task UpdateCommitCountsAndUpstreamAfterPushAsync(int workspaceId, IReadOnlyList<PushRepoPayload> repos, CancellationToken cancellationToken)
    {
        if (repos.Count == 0) return;
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return;

        var repoIds = repos.Select(r => r.RepoId).ToHashSet();
        var links = await _dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId && repoIds.Contains(wr.RepositoryId))
            .ToListAsync(cancellationToken);

        // Persist the remote branch for each pushed repo so it appears in Remotes without calling refresh branches
        var now = DateTime.UtcNow;
        foreach (var wr in links)
        {
            if (string.IsNullOrWhiteSpace(wr.BranchName))
                continue;
            var remoteBranchName = wr.BranchName.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) ? wr.BranchName : "origin/" + wr.BranchName;
            var exists = await _dbContext.RepositoryBranches
                .AnyAsync(rb => rb.WorkspaceRepositoryId == wr.WorkspaceRepositoryId && rb.IsRemote && rb.BranchName == remoteBranchName, cancellationToken);
            if (!exists)
            {
                _dbContext.RepositoryBranches.Add(new RepositoryBranch
                {
                    WorkspaceRepositoryId = wr.WorkspaceRepositoryId,
                    BranchName = remoteBranchName,
                    IsRemote = true,
                    LastSeenAt = now,
                    IsDefault = false
                });
            }
            wr.BranchHasUpstream = true;
        }

        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);

        var results = await Task.WhenAll(repos.Select(async repo =>
        {
            try
            {
                var response = await _agentBridge.SendCommandAsync("GetCommitCounts", new
                {
                    workspaceName = workspace.Name,
                    repositoryName = repo.RepoName,
                    workspaceRoot
                }, cancellationToken);
                if (!response.Success || response.Data == null)
                    return (RepoId: repo.RepoId, Outgoing: (int?)null, Incoming: (int?)null, HasUpstream: (bool?)null, DefaultBehind: (int?)null, DefaultAhead: (int?)null);
                var data = AgentResponseJson.DeserializeAgentResponse<AgentCommitCountsResponse>(response.Data);
                return (RepoId: repo.RepoId, Outgoing: data?.OutgoingCommits, Incoming: data?.IncomingCommits, HasUpstream: data?.HasUpstream, DefaultBehind: data?.DefaultBranchBehind, DefaultAhead: data?.DefaultBranchAhead);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetCommitCounts failed for repo {RepoId} ({RepoName})", repo.RepoId, repo.RepoName);
                return (RepoId: repo.RepoId, Outgoing: (int?)null, Incoming: (int?)null, HasUpstream: (bool?)null, DefaultBehind: (int?)null, DefaultAhead: (int?)null);
            }
        }));

        var resultByRepo = results.ToDictionary(r => r.RepoId);
        foreach (var wr in links)
        {
            if (resultByRepo.TryGetValue(wr.RepositoryId, out var r))
            {
                wr.OutgoingCommits = r.Outgoing;
                wr.IncomingCommits = r.Incoming;
                if (r.HasUpstream.HasValue)
                    wr.BranchHasUpstream = r.HasUpstream.Value;
                if (r.DefaultBehind.HasValue) wr.DefaultBranchBehindCommits = r.DefaultBehind;
                if (r.DefaultAhead.HasValue) wr.DefaultBranchAheadCommits = r.DefaultAhead;
            }
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, cancellationToken);

        if (_hubContext != null)
            await _hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId);
    }
}

