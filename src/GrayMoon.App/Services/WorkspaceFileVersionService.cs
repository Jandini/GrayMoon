using System.Collections.Concurrent;
using System.Diagnostics;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services;

public sealed class WorkspaceFileVersionService(
    IAgentBridge agentBridge,
    WorkspaceService workspaceService,
    WorkspaceRepository workspaceRepository,
    WorkspaceFileVersionConfigRepository versionConfigRepository,
    AppDbContext dbContext,
    ILogger<WorkspaceFileVersionService> logger)
{
    private static readonly ConcurrentDictionary<int, object> CheckLocks = new();
    private static readonly ConcurrentDictionary<int, Task?> InFlightChecks = new();
    /// <summary>
    /// For every file in the workspace that has a version pattern configured:
    ///   1. Resolves the current version for each repo from the workspace's repository links (DB state); no GitVersion is run.
    ///   2. Calls UpdateFileVersions on the agent with those versions in the request to perform the in-place substitution.
    /// When <paramref name="selectedRepositoryIds"/> is set, only files in those repositories are updated.
    /// By default, version-pattern token lines are also filtered to selected repo names; set
    /// <paramref name="filterPatternTokensToSelectedRepositories"/> to false to keep all token lines
    /// while still limiting which files are updated.
    /// Returns (updatedLineCount, failedFileCount, fatalError, list of (RepositoryId, RepoName, FilePath) for each file that was updated).
    /// </summary>
    public async Task<(int Updated, int Failed, string? Error, IReadOnlyList<(int RepositoryId, string RepoName, string FilePath)> UpdatedFiles)> UpdateAllVersionsAsync(
        int workspaceId,
        IReadOnlySet<int>? selectedRepositoryIds = null,
        bool filterPatternTokensToSelectedRepositories = true,
        Action<string>? onFileUpdated = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return (0, 0, "Workspace not found.", []);
        if (!agentBridge.IsAgentConnected) return (0, 0, "Agent is not connected.", []);

        var configs = await versionConfigRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (configs.Count == 0) return (0, 0, "No version configurations found. Use Configure on a file first.", []);

        HashSet<string>? selectedRepoNames = null;
        if (selectedRepositoryIds != null && selectedRepositoryIds.Count > 0)
        {
            selectedRepoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in workspace.Repositories)
            {
                if (link.RepositoryId != 0 && selectedRepositoryIds.Contains(link.RepositoryId) && !string.IsNullOrEmpty(link.Repository?.RepositoryName))
                    selectedRepoNames.Add(link.Repository.RepositoryName);
            }
            if (selectedRepoNames.Count == 0) return (0, 0, "No selected repositories.", []);
        }

        // Collect all unique repo-name tokens used across all patterns
        var repoNamesInUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in configs)
        {
            foreach (var token in ExtractTokens(cfg.VersionPattern))
                repoNamesInUse.Add(token);
        }
        if (selectedRepoNames != null && filterPatternTokensToSelectedRepositories)
            repoNamesInUse.IntersectWith(selectedRepoNames);

        // Build repo name -> version from workspace links (DB state). No GitVersion is run.
        var repoVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in workspace.Repositories)
        {
            if (link.Repository == null || string.IsNullOrEmpty(link.GitVersion)) continue;
            var name = link.Repository.RepositoryName;
            if (!repoNamesInUse.Contains(name)) continue;
            repoVersions[name] = link.GitVersion;
        }
        foreach (var repoName in repoNamesInUse)
        {
            if (!repoVersions.ContainsKey(repoName))
                logger.LogWarning("No version in workspace for repo {RepoName}; version pattern tokens for this repo will be skipped.", repoName);
        }

        // Update each configured file
        var totalUpdated = 0;
        var totalFailed = 0;
        var updatedFiles = new List<(int RepositoryId, string RepoName, string FilePath)>();

        foreach (var cfg in configs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = cfg.File;
            if (file?.Repository == null) continue;

            if (selectedRepositoryIds != null && selectedRepositoryIds.Count > 0 && !selectedRepositoryIds.Contains(file.RepositoryId))
                continue;

            var versionPatternToSend = cfg.VersionPattern;
            if (selectedRepoNames != null && filterPatternTokensToSelectedRepositories)
            {
                versionPatternToSend = FilterPatternLinesToRepos(cfg.VersionPattern, selectedRepoNames);
                if (string.IsNullOrWhiteSpace(versionPatternToSend)) continue;
            }

            try
            {
                var workspaceRoot2 = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
                var resp = await agentBridge.SendCommandAsync("UpdateFileVersions", new
                {
                    workspaceName = workspace.Name,
                    repositoryName = file.Repository.RepositoryName,
                    filePath = file.FilePath,
                    versionPattern = versionPatternToSend,
                    repoVersions,
                    workspaceRoot = workspaceRoot2
                }, cancellationToken);

                if (resp.Success && resp.Data != null)
                {
                    var result = AgentResponseJson.DeserializeAgentResponse<AgentUpdateFileVersionsResponse>(resp.Data);
                    var updatedForFile = result?.UpdatedCount ?? 0;
                    totalUpdated += updatedForFile;
                    if (updatedForFile > 0 && file.FilePath != null)
                    {
                        if (onFileUpdated != null)
                            onFileUpdated(file.FilePath);
                        updatedFiles.Add((file.RepositoryId, file.Repository.RepositoryName ?? "", file.FilePath));
                    }
                    if (result?.ErrorMessage != null)
                        logger.LogWarning("UpdateFileVersions warning for {FilePath}: {Msg}", file.FilePath, result.ErrorMessage);
                }
                else
                {
                    totalFailed++;
                    logger.LogWarning("UpdateFileVersions failed for {FilePath}: {Error}", file.FilePath, resp.Error);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                totalFailed++;
                logger.LogError(ex, "Unexpected error updating versions in {FilePath}", file.FilePath);
            }
        }

        return (totalUpdated, totalFailed, null, updatedFiles);
    }

    /// <summary>Extracts all {token} names from a version pattern string.</summary>
    public static IReadOnlyList<string> ExtractTokens(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return [];
        var tokens = new List<string>();
        foreach (var raw in pattern.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            var start = line.IndexOf('{');
            var end = start >= 0 ? line.IndexOf('}', start) : -1;
            if (start >= 0 && end > start)
                tokens.Add(line[(start + 1)..end]);
        }
        return tokens;
    }

    /// <summary>Removes leading whitespace from each line of the version pattern. Use when saving so stored patterns match without requiring leading spaces.</summary>
    public static string NormalizePatternLeadingWhitespace(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return pattern ?? "";
        var lines = pattern.Split('\n')
            .Select(l => l.TrimEnd('\r').TrimStart())
            .ToList();
        return string.Join("\n", lines);
    }

    /// <summary>Returns version pattern with only lines whose {repositoryName} token is in <paramref name="allowedRepoNames"/>.</summary>
    public static string FilterPatternLinesToRepos(string? versionPattern, IReadOnlySet<string> allowedRepoNames)
    {
        if (string.IsNullOrWhiteSpace(versionPattern) || allowedRepoNames.Count == 0) return string.Empty;
        var lines = new List<string>();
        foreach (var raw in versionPattern.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var start = line.IndexOf('{');
            var end = start >= 0 ? line.IndexOf('}', start) : -1;
            if (start < 0 || end <= start) continue;
            var token = line[(start + 1)..end];
            if (string.IsNullOrEmpty(token) || !allowedRepoNames.Contains(token)) continue;
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Reads all configured version files via the agent, compares current values to expected repo GitVersions,
    /// and persists the results to WorkspaceFileLineStatuses and WorkspaceRepositoryLink (OutOfDateFileLines, OutOfDateFileRepos, TotalFileLines).
    /// Called at the same trigger points as csproj dependency stat recomputation.
    /// Concurrent callers for the same workspace coalesce onto one in-flight check unless <paramref name="forceFresh"/> is true.
    /// </summary>
    public async Task CheckAndPersistFileVersionStatusAsync(int workspaceId, CancellationToken cancellationToken = default, bool forceFresh = false)
    {
        var gate = CheckLocks.GetOrAdd(workspaceId, _ => new object());

        if (forceFresh)
        {
            Task? inFlight = null;
            lock (gate)
            {
                if (InFlightChecks.TryGetValue(workspaceId, out var existing) && existing is { IsCompleted: false })
                    inFlight = existing;
            }
            if (inFlight != null)
            {
                logger.LogDebug("CheckAndPersist forceFresh: awaiting prior in-flight check for workspace {WorkspaceId}", workspaceId);
                try
                {
                    await inFlight.ConfigureAwait(false);
                }
                catch
                {
                    // Prior check failed; still run a fresh check below.
                }
            }
        }

        Task checkTask;
        lock (gate)
        {
            if (!forceFresh && InFlightChecks.TryGetValue(workspaceId, out var existing) && existing is { IsCompleted: false })
            {
                logger.LogDebug("CheckAndPersist coalesced: joining in-flight check for workspace {WorkspaceId}", workspaceId);
                checkTask = existing;
            }
            else
            {
                checkTask = CheckAndPersistFileVersionStatusCoreAsync(workspaceId, cancellationToken);
                InFlightChecks[workspaceId] = checkTask;
            }
        }

        await AwaitAndClearInFlightAsync(workspaceId, checkTask, gate).ConfigureAwait(false);
    }

    private static async Task AwaitAndClearInFlightAsync(int workspaceId, Task checkTask, object gate)
    {
        try
        {
            await checkTask.ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                if (InFlightChecks.TryGetValue(workspaceId, out var current) && ReferenceEquals(current, checkTask))
                    InFlightChecks.TryRemove(workspaceId, out _);
            }
        }
    }

    private async Task CheckAndPersistFileVersionStatusCoreAsync(int workspaceId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        logger.LogDebug("CheckAndPersist starting for workspace {WorkspaceId}", workspaceId);

        if (!agentBridge.IsAgentConnected)
            return;

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return;

        var configs = await versionConfigRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);

        // Build expected version map: repo name -> GitVersion
        var repoVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in workspace.Repositories)
        {
            if (link.Repository == null || string.IsNullOrEmpty(link.GitVersion)) continue;
            repoVersions[link.Repository.RepositoryName] = link.GitVersion;
        }

        // Build per-file check items (skip files whose tokens have no version in the workspace)
        var items = new List<object>();
        foreach (var cfg in configs)
        {
            if (cfg.File?.Repository == null) continue;
            var tokens = ExtractTokens(cfg.VersionPattern);
            var knownVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens)
            {
                if (repoVersions.TryGetValue(token, out var ver))
                    knownVersions[token] = ver;
            }
            if (knownVersions.Count == 0) continue;

            items.Add(new
            {
                repositoryName = cfg.File.Repository.RepositoryName,
                filePath = cfg.File.FilePath,
                pattern = cfg.VersionPattern,
                expectedVersions = knownVersions
            });
        }

        // Delete existing statuses for this workspace regardless of whether we have items to check
        await dbContext.WorkspaceFileLineStatuses
            .Where(s => s.WorkspaceId == workspaceId)
            .ExecuteDeleteAsync(cancellationToken);

        if (items.Count == 0)
        {
            // Reset counters on all repo links when there are no configured files
            await dbContext.WorkspaceRepositories
                .Where(wr => wr.WorkspaceId == workspaceId && (wr.OutOfDateFileLines != null || wr.OutOfDateFileRepos != null || wr.TotalFileLines != null))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(wr => wr.OutOfDateFileLines, (int?)null)
                    .SetProperty(wr => wr.OutOfDateFileRepos, (int?)null)
                    .SetProperty(wr => wr.TotalFileLines, (int?)null),
                    cancellationToken);
            logger.LogDebug("CheckAndPersist completed for workspace {WorkspaceId} in {ElapsedMs}ms (no items)", workspaceId, sw.ElapsedMilliseconds);
            return;
        }

        var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);

        try
        {
            var agentSw = Stopwatch.StartNew();
            var resp = await agentBridge.SendCommandAsync("CheckFileVersions", new
            {
                workspaceName = workspace.Name,
                workspaceRoot,
                files = items
            }, cancellationToken);
            logger.LogDebug("CheckAndPersist CheckFileVersions agent call completed for workspace {WorkspaceId} in {ElapsedMs}ms", workspaceId, agentSw.ElapsedMilliseconds);

            if (!resp.Success || resp.Data == null)
            {
                logger.LogWarning("CheckFileVersions failed for workspace {WorkspaceId}: {Error}", workspaceId, resp.Error);
                return;
            }

            var result = AgentResponseJson.DeserializeAgentResponse<CheckFileVersionsAgentResponse>(resp.Data);
            if (result?.Files == null) return;

            // Accumulate per-repo stats and new per-file status rows
            var newStatuses = new List<WorkspaceFileLineStatus>();
            var repoOutOfDate = new Dictionary<int, int>();
            var repoOutOfDateTokens = new Dictionary<int, HashSet<string>>();
            var repoTotalMatched = new Dictionary<int, int>();

            foreach (var fileResult in result.Files)
            {
                var expectedCount = fileResult.ExpectedTokenCount;
                if (fileResult.TotalMatchedLines == 0 && expectedCount == 0)
                    continue;

                // Find repository id for this file result
                var repoLink = workspace.Repositories.FirstOrDefault(r =>
                    string.Equals(r.Repository?.RepositoryName, fileResult.RepositoryName, StringComparison.OrdinalIgnoreCase));
                if (repoLink == null) continue;
                var repoId = repoLink.RepositoryId;

                var outOfDateCount = fileResult.OutOfDateLines?.Count ?? 0;
                var totalForStats = fileResult.TotalMatchedLines > 0 ? fileResult.TotalMatchedLines : expectedCount;

                newStatuses.Add(new WorkspaceFileLineStatus
                {
                    WorkspaceId = workspaceId,
                    RepositoryId = repoId,
                    FilePath = fileResult.FilePath ?? "",
                    FileName = fileResult.FileName ?? "",
                    TotalMatchedLines = totalForStats,
                    OutOfDateLines = outOfDateCount
                });

                repoTotalMatched[repoId] = (repoTotalMatched.TryGetValue(repoId, out var tot) ? tot : 0) + totalForStats;
                repoOutOfDate[repoId] = (repoOutOfDate.TryGetValue(repoId, out var ood) ? ood : 0) + outOfDateCount;
                if (outOfDateCount > 0 && fileResult.OutOfDateLines != null)
                {
                    if (!repoOutOfDateTokens.TryGetValue(repoId, out var tokens))
                        repoOutOfDateTokens[repoId] = tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in fileResult.OutOfDateLines)
                    {
                        if (!string.IsNullOrWhiteSpace(line.TokenName))
                            tokens.Add(line.TokenName);
                    }
                }
            }

            if (newStatuses.Count > 0)
            {
                dbContext.WorkspaceFileLineStatuses.AddRange(newStatuses);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            // Update WorkspaceRepositoryLink counters
            var allLinks = await dbContext.WorkspaceRepositories
                .Where(wr => wr.WorkspaceId == workspaceId)
                .ToListAsync(cancellationToken);

            foreach (var link in allLinks)
            {
                link.OutOfDateFileLines = repoOutOfDate.TryGetValue(link.RepositoryId, out var ood) ? ood : (int?)null;
                link.OutOfDateFileRepos = repoOutOfDateTokens.TryGetValue(link.RepositoryId, out var tokens) && tokens.Count > 0
                    ? tokens.Count
                    : (int?)null;
                link.TotalFileLines = repoTotalMatched.TryGetValue(link.RepositoryId, out var tot) ? tot : (int?)null;
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogDebug("CheckAndPersist completed for workspace {WorkspaceId} in {ElapsedMs}ms", workspaceId, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error checking file version status for workspace {WorkspaceId}", workspaceId);
        }
    }

    /// <summary>Returns out-of-date file line statuses for the workspace, grouped by RepositoryId.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<WorkspaceFileLineStatus>>> GetFileLineStatusByWorkspaceAsync(
        int workspaceId, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.WorkspaceFileLineStatuses
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(s => s.RepositoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<WorkspaceFileLineStatus>)g.ToList());
    }

    /// <summary>
    /// Returns per-repo (FileName, TokenName, Version) triples for the OK badge tooltip.
    /// Each entry represents a tracked token in a version file whose expected value equals the current workspace GitVersion.
    /// Only repos present in <paramref name="repoVersionMap"/> contribute entries.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<(string FileName, string TokenName, string Version)>>> GetAllFileVersionLinesByRepoAsync(
        int workspaceId,
        IReadOnlyDictionary<string, string> repoVersionMap,
        CancellationToken cancellationToken = default)
    {
        var configs = await versionConfigRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        var result = new Dictionary<int, List<(string FileName, string TokenName, string Version)>>();

        foreach (var cfg in configs)
        {
            if (cfg.File?.Repository == null) continue;
            var repoId = cfg.File.RepositoryId;
            var fileName = cfg.File.FileName;
            var tokens = ExtractTokens(cfg.VersionPattern);

            foreach (var token in tokens)
            {
                if (!repoVersionMap.TryGetValue(token, out var ver) || string.IsNullOrEmpty(ver)) continue;
                if (!result.TryGetValue(repoId, out var list))
                    result[repoId] = list = [];
                if (!list.Any(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(e.TokenName, token, StringComparison.OrdinalIgnoreCase)))
                    list.Add((fileName, token, ver));
            }
        }

        return result.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<(string, string, string)>)kvp.Value);
    }

    private sealed class CheckFileVersionsAgentResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("files")]
        public List<CheckFileVersionsAgentFileResult>? Files { get; set; }
    }

    private sealed class CheckFileVersionsAgentFileResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("repositoryName")] public string? RepositoryName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("filePath")] public string? FilePath { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("fileName")] public string? FileName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("totalMatchedLines")] public int TotalMatchedLines { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("expectedTokenCount")] public int ExpectedTokenCount { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("outOfDateLines")] public List<CheckFileVersionsAgentOutOfDateLine>? OutOfDateLines { get; set; }
    }

    private sealed class CheckFileVersionsAgentOutOfDateLine
    {
        [System.Text.Json.Serialization.JsonPropertyName("tokenName")] public string? TokenName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("currentValue")] public string? CurrentValue { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("expectedValue")] public string? ExpectedValue { get; set; }
    }
}
