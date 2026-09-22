using System.Collections.Concurrent;
using System.Diagnostics;
using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Features;
using GrayMoon.Application.Features;
using GrayMoon.Common.FileVersions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Workspaces;

public sealed class WorkspaceFileVersionService(
    IAgentBridge agentBridge,
    WorkspaceRepository workspaceRepository,
    WorkspaceProjectRepository workspaceProjectRepository,
    WorkspaceFileVersionConfigRepository versionConfigRepository,
    AppDbContext dbContext,
    IHubContext<WorkspaceSyncHub> hubContext,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    ILogger<WorkspaceFileVersionService> logger)
{
    private static readonly ConcurrentDictionary<string, object> CheckLocks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Task?> InFlightChecks = new(StringComparer.Ordinal);

    private static string CheckKey(int workspaceId, int contextId) => $"{workspaceId}:{contextId}";

    /// <summary>
    /// Returns per-file "missing on disk" flags scoped to <paramref name="contextId"/>: reads
    /// <see cref="WorkspaceFileContextState.IsMissingOnDisk"/> when a row exists for that context, falling back
    /// to the shared <see cref="WorkspaceFile.IsMissingOnDisk"/> only for the special Workspace context (or when
    /// no context-state row has been persisted for that file yet) - same rule as
    /// <c>WorkspaceProjectRepository.GetContextVersionAndLevelByRepoAsync</c>. Used to overlay an in-memory,
    /// detached (AsNoTracking) <see cref="WorkspaceFile"/>/<see cref="WorkspaceFileVersionConfig"/> graph so a
    /// Feature's decisions (skip missing file, counters, generated-package sync) never read the Workspace's own
    /// flag, and vice versa - see AGENTS.md "Feature-context scoping".
    /// </summary>
    public async Task<Dictionary<int, bool?>> GetMissingFlagsByFileIdAsync(
        int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
    {
        var isSpecialWorkspace = await dbContext.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(c => c.WorkspaceFeatureContextId == contextId.Value)
            .Select(c => c.Kind == WorkspaceFeatureContextKind.Workspace)
            .FirstOrDefaultAsync(cancellationToken);

        var files = await dbContext.WorkspaceFiles
            .AsNoTracking()
            .Where(f => f.WorkspaceId == workspaceId)
            .Select(f => new { f.FileId, f.IsMissingOnDisk })
            .ToListAsync(cancellationToken);
        var result = new Dictionary<int, bool?>();
        if (files.Count == 0)
            return result;

        var fileIds = files.Select(f => f.FileId).ToList();
        var states = await dbContext.WorkspaceFileContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == contextId.Value && fileIds.Contains(s.FileId))
            .ToDictionaryAsync(s => s.FileId, cancellationToken);

        foreach (var f in files)
        {
            if (states.TryGetValue(f.FileId, out var state))
                result[f.FileId] = state.IsMissingOnDisk;
            else if (isSpecialWorkspace)
                result[f.FileId] = f.IsMissingOnDisk;
            else
                result[f.FileId] = null;
        }
        return result;
    }

    /// <summary>
    /// Overlays <paramref name="missingFlagsByFileId"/> onto each config's <c>File.IsMissingOnDisk</c> in place.
    /// Safe because <see cref="WorkspaceFileVersionConfigRepository.GetByWorkspaceIdAsync"/> returns AsNoTracking
    /// (detached) entities - mutating them here never risks a later <c>SaveChangesAsync</c> persisting the
    /// overlay back onto the shared row.
    /// </summary>
    private static void ApplyMissingFlagOverlay(IEnumerable<WorkspaceFileVersionConfig> configs, IReadOnlyDictionary<int, bool?> missingFlagsByFileId)
    {
        foreach (var cfg in configs)
        {
            if (cfg.File == null) continue;
            if (missingFlagsByFileId.TryGetValue(cfg.File.FileId, out var flag))
                cfg.File.IsMissingOnDisk = flag;
        }
    }

    /// <summary>
    /// Removes coalescing gates for every context of <paramref name="workspaceId"/>.
    /// Call when a workspace is deleted.
    /// </summary>
    public static void RemoveWorkspaceCheckLock(int workspaceId)
    {
        var prefix = $"{workspaceId}:";
        foreach (var key in CheckLocks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            CheckLocks.TryRemove(key, out _);
        foreach (var key in InFlightChecks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            InFlightChecks.TryRemove(key, out _);
    }

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
        var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        return await UpdateAllVersionsAsync(
            workspaceId, contextId, selectedRepositoryIds, filterPatternTokensToSelectedRepositories, onFileUpdated, cancellationToken);
    }

    public async Task<(int Updated, int Failed, string? Error, IReadOnlyList<(int RepositoryId, string RepoName, string FilePath)> UpdatedFiles)> UpdateAllVersionsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
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

        var missingFlags = await GetMissingFlagsByFileIdAsync(workspaceId, contextId, cancellationToken);
        ApplyMissingFlagOverlay(configs, missingFlags);

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

        var patternsForResolve = new List<string>();
        foreach (var cfg in configs)
        {
            if (cfg.File?.Repository == null || cfg.File.IsMissingOnDisk == true) continue;
            if (selectedRepositoryIds != null && selectedRepositoryIds.Count > 0 && !selectedRepositoryIds.Contains(cfg.File.RepositoryId))
                continue;

            var pattern = cfg.VersionPattern;
            if (selectedRepoNames != null && filterPatternTokensToSelectedRepositories)
            {
                pattern = FilterPatternLinesToRepos(cfg.VersionPattern, selectedRepoNames);
                if (string.IsNullOrWhiteSpace(pattern)) continue;
            }
            patternsForResolve.Add(pattern);
        }

        var (workspaceRoot, workspaceFolderName) = await pathResolver.GetAgentWorkspaceArgsAsync(contextId, cancellationToken);
        var tokenValues = await ResolveTokenValuesAsync(workspace, contextId, workspaceRoot, workspaceFolderName, patternsForResolve, cancellationToken);

        // Update each configured file
        var totalUpdated = 0;
        var totalFailed = 0;
        var updatedFiles = new List<(int RepositoryId, string RepoName, string FilePath)>();

        foreach (var cfg in configs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = cfg.File;
            if (file?.Repository == null) continue;

            if (file.IsMissingOnDisk == true)
                continue;

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
                var resp = await agentBridge.SendCommandAsync("UpdateFileVersions", new
                {
                    workspaceName = workspaceFolderName,
                    repositoryName = file.Repository.RepositoryName,
                    filePath = file.FilePath,
                    versionPattern = versionPatternToSend,
                    tokenValues,
                    workspaceRoot
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
                    {
                        if (result.ErrorMessage.StartsWith("File not found:", StringComparison.OrdinalIgnoreCase))
                            logger.LogDebug("UpdateFileVersions skipped missing file {FilePath}", file.FilePath);
                        else
                            logger.LogWarning("UpdateFileVersions warning for {FilePath}: {Msg}", file.FilePath, result.ErrorMessage);
                    }
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

    /// <summary>Extracts structured file-version tokens from a version pattern string.</summary>
    public static IReadOnlyList<FileVersionToken> ExtractTokens(string? pattern)
        => FileVersionTokenParser.ExtractTokens(pattern);

    /// <summary>Removes leading whitespace from each line of the version pattern. Use when saving so stored patterns match without requiring leading spaces.</summary>
    public static string NormalizePatternLeadingWhitespace(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return pattern ?? "";
        var lines = pattern.Split('\n')
            .Select(l => l.TrimEnd('\r').TrimStart())
            .ToList();
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Returns version pattern with only lines whose token's <see cref="FileVersionToken.RepositoryName"/>
    /// is in <paramref name="allowedRepoNames"/> (selectors such as <c>:commit</c> do not affect matching).
    /// </summary>
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
            var inner = line[(start + 1)..end];
            if (!FileVersionTokenParser.TryParse(inner, out var token, out _) || token == null) continue;
            if (!allowedRepoNames.Contains(token.RepositoryName)) continue;
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Resolves all token values for the given patterns: GitVersion and branch from workspace links,
    /// commit SHAs via one batched Agent <c>GetHeadCommits</c> call for repositories that need them.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveTokenValuesAsync(
        Workspace workspace,
        WorkspaceFeatureContextId contextId,
        string workspaceRoot,
        string workspaceFolderName,
        IEnumerable<string?> patterns,
        CancellationToken cancellationToken)
    {
        var tokensByKey = new Dictionary<string, FileVersionToken>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            foreach (var token in FileVersionTokenParser.ExtractTokens(pattern))
                tokensByKey.TryAdd(token.TokenKey, token);
        }

        var linksByName = new Dictionary<string, WorkspaceRepositoryLink>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in workspace.Repositories)
        {
            if (link.Repository == null || string.IsNullOrEmpty(link.Repository.RepositoryName)) continue;
            linksByName.TryAdd(link.Repository.RepositoryName, link);
        }

        var contextInfo = await contextResolver.GetRequiredAsync(contextId, workspace.WorkspaceId, cancellationToken);
        Dictionary<int, WorkspaceRepositoryContextState>? contextStates = null;
        if (!contextInfo.IsSpecialWorkspace)
        {
            contextStates = await dbContext.WorkspaceRepositoryContextStates
                .AsNoTracking()
                .Where(s => s.WorkspaceFeatureContextId == contextId.Value)
                .ToDictionaryAsync(s => s.WorkspaceRepositoryId, cancellationToken);
        }

        var tokenValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var commitRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokensByKey.Values)
        {
            if (!linksByName.TryGetValue(token.RepositoryName, out var link))
            {
                logger.LogWarning(
                    "No workspace repository named {RepoName}; file-version token {TokenKey} will be skipped.",
                    token.RepositoryName, token.TokenKey);
                continue;
            }

            var gitVersion = link.GitVersion;
            var branchName = link.BranchName;
            if (contextStates != null
                && contextStates.TryGetValue(link.WorkspaceRepositoryId, out var state))
            {
                gitVersion = state.GitVersion ?? gitVersion;
                branchName = state.BranchName ?? branchName;
            }

            switch (token.Kind)
            {
                case FileVersionTokenKind.GitVersion:
                    if (!string.IsNullOrEmpty(gitVersion))
                        tokenValues[token.TokenKey] = gitVersion;
                    else
                        logger.LogWarning(
                            "No GitVersion in workspace for repo {RepoName}; token {TokenKey} will be skipped.",
                            token.RepositoryName, token.TokenKey);
                    break;

                case FileVersionTokenKind.Branch:
                    if (!string.IsNullOrEmpty(branchName))
                        tokenValues[token.TokenKey] = branchName;
                    else
                        logger.LogWarning(
                            "No branch name in workspace for repo {RepoName} (detached HEAD or unset); token {TokenKey} will be skipped and the existing file value left unchanged.",
                            token.RepositoryName, token.TokenKey);
                    break;

                case FileVersionTokenKind.Commit:
                    commitRepos.Add(token.RepositoryName);
                    break;
            }
        }

        if (commitRepos.Count > 0 && agentBridge.IsAgentConnected)
        {
            try
            {
                var resp = await agentBridge.SendCommandAsync(AgentHubMethods.GetHeadCommits, new
                {
                    workspaceName = workspaceFolderName,
                    workspaceRoot,
                    repositoryNames = commitRepos.ToList()
                }, cancellationToken);

                if (resp.Success && resp.Data != null)
                {
                    var result = AgentResponseJson.DeserializeAgentResponse<GetHeadCommitsAgentResponse>(resp.Data);
                    var commits = new Dictionary<string, string>(
                        result?.Commits ?? [],
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var repoName in commitRepos)
                    {
                        var tokenKey = new FileVersionToken(repoName, FileVersionTokenKind.Commit).TokenKey;
                        if (commits.TryGetValue(repoName, out var sha) && !string.IsNullOrWhiteSpace(sha))
                            tokenValues[tokenKey] = sha;
                        else
                            logger.LogWarning(
                                "Could not resolve HEAD commit for repo {RepoName}; token {TokenKey} will be skipped and the existing file value left unchanged.",
                                repoName, tokenKey);
                    }
                }
                else
                {
                    logger.LogWarning(
                        "GetHeadCommits failed for workspace {WorkspaceName}: {Error}. Commit tokens will be skipped.",
                        workspace.Name, resp.Error);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "GetHeadCommits failed for workspace {WorkspaceName}; commit tokens will be skipped.", workspace.Name);
            }
        }
        else if (commitRepos.Count > 0)
        {
            logger.LogWarning("Agent is not connected; skipping {Count} :commit token(s).", commitRepos.Count);
        }

        return tokenValues;
    }

    private sealed class GetHeadCommitsAgentResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("commits")]
        public Dictionary<string, string>? Commits { get; set; }
    }

    /// <summary>
    /// Reads all configured version files via the agent, compares current values to expected repo GitVersions,
    /// and persists the results to WorkspaceFileLineStatuses and WorkspaceRepositoryLink file-config counters.
    /// Called at the same trigger points as csproj dependency stat recomputation.
    /// Concurrent callers for the same workspace coalesce onto one in-flight check unless <paramref name="forceFresh"/> is true.
    /// </summary>
    public async Task CheckAndPersistFileVersionStatusAsync(int workspaceId, CancellationToken cancellationToken = default, bool forceFresh = false)
    {
        var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        await CheckAndPersistFileVersionStatusAsync(workspaceId, contextId, cancellationToken, forceFresh);
    }

    public async Task CheckAndPersistFileVersionStatusAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default,
        bool forceFresh = false)
    {
        var key = CheckKey(workspaceId, contextId.Value);
        var gate = CheckLocks.GetOrAdd(key, _ => new object());

        if (forceFresh)
        {
            Task? inFlight = null;
            lock (gate)
            {
                if (InFlightChecks.TryGetValue(key, out var existing) && existing is { IsCompleted: false })
                    inFlight = existing;
            }
            if (inFlight != null)
            {
                logger.LogDebug("CheckAndPersist forceFresh: awaiting prior in-flight check for workspace {WorkspaceId} context {ContextId}", workspaceId, contextId.Value);
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
            if (!forceFresh && InFlightChecks.TryGetValue(key, out var existing) && existing is { IsCompleted: false })
            {
                logger.LogDebug("CheckAndPersist coalesced: joining in-flight check for workspace {WorkspaceId} context {ContextId}", workspaceId, contextId.Value);
                checkTask = existing;
            }
            else
            {
                checkTask = CheckAndPersistFileVersionStatusCoreAsync(workspaceId, contextId, cancellationToken);
                InFlightChecks[key] = checkTask;
            }
        }

        await AwaitAndClearInFlightAsync(key, checkTask, gate).ConfigureAwait(false);
    }

    private static async Task AwaitAndClearInFlightAsync(string key, Task checkTask, object gate)
    {
        try
        {
            await checkTask.ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                if (InFlightChecks.TryGetValue(key, out var current) && ReferenceEquals(current, checkTask))
                    InFlightChecks.TryRemove(key, out _);
            }
        }
    }

    private async Task CheckAndPersistFileVersionStatusCoreAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        logger.LogDebug("CheckAndPersist starting for workspace {WorkspaceId} context {ContextId}", workspaceId, contextId.Value);

        if (!agentBridge.IsAgentConnected)
            return;

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return;

        var contextInfo = await contextResolver.GetRequiredAsync(contextId, workspaceId, cancellationToken);

        if (await SyncGeneratedPackageDependenciesAsync(workspaceId, contextId, cancellationToken))
            await workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, contextId.Value, cancellationToken);

        var configs = await versionConfigRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        ApplyMissingFlagOverlay(configs, await GetMissingFlagsByFileIdAsync(workspaceId, contextId, cancellationToken));
        var trackedFiles = await dbContext.WorkspaceFiles
            .Where(f => f.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        var nameToRepoId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in workspace.Repositories)
        {
            if (link.Repository != null && !string.IsNullOrEmpty(link.Repository.RepositoryName))
            {
                var name = link.Repository.RepositoryName.Trim();
                if (!nameToRepoId.ContainsKey(name))
                    nameToRepoId[name] = link.RepositoryId;
            }
        }

        var (workspaceRoot, workspaceFolderName) = await pathResolver.GetAgentWorkspaceArgsAsync(contextId, cancellationToken);
        var patterns = configs
            .Where(c => c.File?.Repository != null)
            .Select(c => c.VersionPattern)
            .ToList();
        var tokenValues = await ResolveTokenValuesAsync(workspace, contextId, workspaceRoot, workspaceFolderName, patterns, cancellationToken);

        var items = new List<object>();
        foreach (var cfg in configs)
        {
            if (cfg.File?.Repository == null) continue;
            var tokens = ExtractTokens(cfg.VersionPattern);
            var knownValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens)
            {
                if (tokenValues.TryGetValue(token.TokenKey, out var value))
                    knownValues[token.TokenKey] = value;
            }
            if (knownValues.Count == 0) continue;

            items.Add(new
            {
                repositoryName = cfg.File.Repository.RepositoryName,
                filePath = cfg.File.FilePath,
                pattern = cfg.VersionPattern,
                expectedValues = knownValues
            });
        }

        var missingFlagChanged = false;

        if (items.Count == 0)
        {
            await ApplyFileConfigLinkCountersAsync(
                workspaceId,
                contextId,
                contextInfo.IsSpecialWorkspace,
                configs,
                nameToRepoId,
                repoOutOfDateTokens: new Dictionary<int, HashSet<string>>(),
                cancellationToken);

            await dbContext.WorkspaceFileLineStatuses
                .Where(s => s.WorkspaceId == workspaceId && s.WorkspaceFeatureContextId == contextId.Value)
                .ExecuteDeleteAsync(cancellationToken);

            await hubContext.Clients.All.SendAsync("ContextSynced", workspaceId, contextId.Value, cancellationToken);
            if (contextInfo.IsSpecialWorkspace)
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
            logger.LogDebug("CheckAndPersist completed for workspace {WorkspaceId} context {ContextId} in {ElapsedMs}ms (no items)", workspaceId, contextId.Value, sw.ElapsedMilliseconds);
            return;
        }

        try
        {
            var agentSw = Stopwatch.StartNew();
            var resp = await agentBridge.SendCommandAsync("CheckFileVersions", new
            {
                workspaceName = workspaceFolderName,
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

            var fileByRepoAndPath = trackedFiles.ToDictionary(
                f => (RepoId: f.RepositoryId, Path: f.FilePath),
                f => f);

            var repoOutOfDateTokens = new Dictionary<int, HashSet<string>>();
            var newStatuses = new List<WorkspaceFileLineStatus>();
            var seenStaleTokens = new Dictionary<int, HashSet<string>>();

            foreach (var fileResult in result.Files)
            {
                var repoLink = workspace.Repositories.FirstOrDefault(r =>
                    string.Equals(r.Repository?.RepositoryName, fileResult.RepositoryName, StringComparison.OrdinalIgnoreCase));
                if (repoLink == null) continue;
                var repoId = repoLink.RepositoryId;

                if (fileByRepoAndPath.TryGetValue((repoId, fileResult.FilePath ?? ""), out var trackedFile))
                {
                    var isMissing = fileResult.FileMissing;

                    // Context-scoped write (§7/AGENTS.md "Feature-context scoping"): persist onto this
                    // context's own WorkspaceFileContextState row, get-or-create. Only mirror onto the shared
                    // WorkspaceFile.IsMissingOnDisk when this context is the special Workspace - a Feature's
                    // file-missing check must never overwrite the Workspace's own flag (or another Feature's).
                    var state = await dbContext.WorkspaceFileContextStates
                        .FirstOrDefaultAsync(
                            s => s.WorkspaceFeatureContextId == contextId.Value && s.FileId == trackedFile.FileId,
                            cancellationToken);
                    if (state is null)
                    {
                        state = new WorkspaceFileContextState
                        {
                            WorkspaceFeatureContextId = contextId.Value,
                            FileId = trackedFile.FileId
                        };
                        dbContext.WorkspaceFileContextStates.Add(state);
                    }
                    var wasMissingForContext = state.IsMissingOnDisk == true;
                    state.IsMissingOnDisk = isMissing ? true : null;
                    state.LastCheckedAt = DateTime.UtcNow;
                    if (wasMissingForContext != isMissing)
                        missingFlagChanged = true;

                    if (contextInfo.IsSpecialWorkspace)
                        trackedFile.IsMissingOnDisk = isMissing ? true : null;

                    // Keep the in-memory (detached) config's overlay consistent with what was just persisted,
                    // so the counters computed below from `configs` reflect this context's own answer rather
                    // than the shared row - mirrors the same overlay applied at the top of every other method
                    // in this file via ApplyMissingFlagOverlay/GetMissingFlagsByFileIdAsync.
                    foreach (var relatedCfg in configs.Where(c => c.File?.FileId == trackedFile.FileId))
                        relatedCfg.File!.IsMissingOnDisk = state.IsMissingOnDisk;
                }

                if (fileResult.FileMissing)
                    continue;

                if (fileResult.OutOfDateLines is not { Count: > 0 })
                    continue;

                if (!seenStaleTokens.TryGetValue(repoId, out var seen))
                    seenStaleTokens[repoId] = seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (!repoOutOfDateTokens.TryGetValue(repoId, out var staleTokens))
                    repoOutOfDateTokens[repoId] = staleTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var line in fileResult.OutOfDateLines)
                {
                    if (string.IsNullOrWhiteSpace(line.TokenName)) continue;
                    if (!seen.Add(line.TokenName)) continue;

                    staleTokens.Add(line.TokenName);
                    newStatuses.Add(new WorkspaceFileLineStatus
                    {
                        WorkspaceId = workspaceId,
                        WorkspaceFeatureContextId = contextId.Value,
                        RepositoryId = repoId,
                        FilePath = fileResult.FilePath ?? "",
                        FileName = fileResult.FileName ?? "",
                        TokenName = line.TokenName,
                        CurrentValue = line.CurrentValue,
                        ExpectedValue = line.ExpectedValue
                    });
                }
            }

            await dbContext.WorkspaceFileLineStatuses
                .Where(s => s.WorkspaceId == workspaceId && s.WorkspaceFeatureContextId == contextId.Value)
                .ExecuteDeleteAsync(cancellationToken);

            if (newStatuses.Count > 0)
            {
                dbContext.WorkspaceFileLineStatuses.AddRange(newStatuses);
            }

            await ApplyFileConfigLinkCountersAsync(
                workspaceId, contextId, contextInfo.IsSpecialWorkspace, configs, nameToRepoId, repoOutOfDateTokens, cancellationToken);

            if (missingFlagChanged)
                await workspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, contextId.Value, cancellationToken);

            await hubContext.Clients.All.SendAsync("ContextSynced", workspaceId, contextId.Value, cancellationToken);
            if (contextInfo.IsSpecialWorkspace)
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
            logger.LogDebug("CheckAndPersist completed for workspace {WorkspaceId} context {ContextId} in {ElapsedMs}ms", workspaceId, contextId.Value, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error checking file version status for workspace {WorkspaceId}", workspaceId);
        }
    }

    /// <summary>
    /// Detects virtual/generated NuGet package dependencies from configured .csproj version files: for every
    /// configured file whose path is a .csproj, asks the agent to resolve which PackageReference (Include name)
    /// each version-pattern line refers to (via the real .csproj's XML-based PackageReference parsing, not
    /// line-based text matching), resolves the producer repository from the pattern's repo-name token, and syncs
    /// the resulting generated <see cref="WorkspaceProject"/>/<see cref="ProjectDependency"/> rows.
    /// Returns true if the agent call succeeded (regardless of whether any generated dependency changed).
    /// </summary>
    public async Task<bool> SyncGeneratedPackageDependenciesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        if (!agentBridge.IsAgentConnected) return false;

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return false;

        var configs = await versionConfigRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        var missingFlags = await GetMissingFlagsByFileIdAsync(workspaceId, contextId, cancellationToken);
        ApplyMissingFlagOverlay(configs, missingFlags);
        var csprojConfigs = configs
            .Where(c => c.File?.Repository != null
                && c.File.IsMissingOnDisk != true
                && c.File.FilePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(c.VersionPattern))
            .ToList();

        if (csprojConfigs.Count == 0)
        {
            await workspaceProjectRepository.SyncGeneratedPackageDependenciesAsync(workspaceId, [], cancellationToken);
            return true;
        }

        var nameToRepoId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in workspace.Repositories)
        {
            if (link.Repository != null && !string.IsNullOrEmpty(link.Repository.RepositoryName))
            {
                var name = link.Repository.RepositoryName.Trim();
                if (!nameToRepoId.ContainsKey(name))
                    nameToRepoId[name] = link.RepositoryId;
            }
        }

        var (workspaceRoot, workspaceFolderName) = await pathResolver.GetAgentWorkspaceArgsAsync(contextId, cancellationToken);

        var requestItems = csprojConfigs
            .Select(cfg => new
            {
                repositoryName = cfg.File!.Repository!.RepositoryName,
                filePath = cfg.File.FilePath,
                pattern = cfg.VersionPattern
            })
            .ToList();

        try
        {
            var resp = await agentBridge.SendCommandAsync(AgentHubMethods.ResolveGeneratedPackageReferences, new
            {
                workspaceName = workspaceFolderName,
                workspaceRoot,
                files = requestItems
            }, cancellationToken);

            if (!resp.Success || resp.Data == null)
            {
                logger.LogWarning("ResolveGeneratedPackageReferences failed for workspace {WorkspaceId}: {Error}", workspaceId, resp.Error);
                return false;
            }

            var result = AgentResponseJson.DeserializeAgentResponse<ResolveGeneratedPackageReferencesAgentResponse>(resp.Data);
            if (result?.Files == null)
                return false;

            var resolved = new List<GeneratedPackageDependencyInfo>();
            foreach (var fileResult in result.Files)
            {
                if (fileResult.Packages is not { Count: > 0 }) continue;

                var matchingCfg = csprojConfigs.FirstOrDefault(cfg =>
                    string.Equals(cfg.File!.Repository!.RepositoryName, fileResult.RepositoryName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(cfg.File.FilePath, fileResult.FilePath, StringComparison.OrdinalIgnoreCase));
                if (matchingCfg == null) continue;

                var consumerRepositoryId = matchingCfg.File!.RepositoryId;
                foreach (var pkg in fileResult.Packages)
                {
                    if (string.IsNullOrWhiteSpace(pkg.RepoNameToken) || string.IsNullOrWhiteSpace(pkg.PackageName)) continue;
                    if (!nameToRepoId.TryGetValue(pkg.RepoNameToken.Trim(), out var producerRepositoryId)) continue;

                    resolved.Add(new GeneratedPackageDependencyInfo(
                        consumerRepositoryId,
                        matchingCfg.File.FilePath,
                        producerRepositoryId,
                        pkg.PackageName,
                        string.IsNullOrWhiteSpace(pkg.Version) ? null : pkg.Version.Trim()));
                }
            }

            await workspaceProjectRepository.SyncGeneratedPackageDependenciesAsync(workspaceId, resolved, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error syncing generated package dependencies for workspace {WorkspaceId}", workspaceId);
            return false;
        }
    }

    private sealed class ResolveGeneratedPackageReferencesAgentResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("files")]
        public List<ResolveGeneratedPackageReferencesAgentFileResult>? Files { get; set; }
    }

    private sealed class ResolveGeneratedPackageReferencesAgentFileResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("repositoryName")] public string? RepositoryName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("filePath")] public string? FilePath { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("packages")] public List<ResolveGeneratedPackageReferencesAgentPackageEntry>? Packages { get; set; }
    }

    private sealed class ResolveGeneratedPackageReferencesAgentPackageEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("repoNameToken")] public string? RepoNameToken { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("packageName")] public string? PackageName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("version")] public string? Version { get; set; }
    }

    private async Task ApplyFileConfigLinkCountersAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        bool isSpecialWorkspace,
        IReadOnlyList<WorkspaceFileVersionConfig> configs,
        Dictionary<string, int> nameToRepoId,
        Dictionary<int, HashSet<string>> repoOutOfDateTokens,
        CancellationToken cancellationToken)
    {
        var totalConfigRepos = BuildTotalFileConfigReposByDependentRepo(configs, nameToRepoId);
        var selfReferencingRepoIds = BuildSelfReferencingRepoIds(configs, nameToRepoId);

        var allLinks = await dbContext.WorkspaceRepositories
            .Where(wr => wr.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        foreach (var link in allLinks)
        {
            var outOfDate = repoOutOfDateTokens.TryGetValue(link.RepositoryId, out var tokens) && tokens.Count > 0
                ? tokens.Count
                : (int?)null;
            var totalConfig = totalConfigRepos.TryGetValue(link.RepositoryId, out var total) && total.Count > 0
                ? total.Count
                : (int?)null;
            var hasSelf = selfReferencingRepoIds.Contains(link.RepositoryId) ? true : (bool?)null;

            if (isSpecialWorkspace)
            {
                link.OutOfDateFileRepos = outOfDate;
                link.OutOfDateFileLines = null;
                link.TotalFileLines = null;
                link.TotalFileConfigRepos = totalConfig;
                link.HasSelfFileVersionToken = hasSelf;
            }

            var state = await dbContext.WorkspaceRepositoryContextStates
                .FirstOrDefaultAsync(
                    s => s.WorkspaceFeatureContextId == contextId.Value && s.WorkspaceRepositoryId == link.WorkspaceRepositoryId,
                    cancellationToken);
            if (state is null)
            {
                state = new WorkspaceRepositoryContextState
                {
                    WorkspaceFeatureContextId = contextId.Value,
                    WorkspaceRepositoryId = link.WorkspaceRepositoryId
                };
                dbContext.WorkspaceRepositoryContextStates.Add(state);
            }

            state.OutOfDateFileRepos = outOfDate;
            state.OutOfDateFileLines = null;
            state.TotalFileLines = null;
            state.TotalFileConfigRepos = totalConfig;
            state.HasSelfFileVersionToken = hasSelf;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Dictionary<int, HashSet<string>> BuildTotalFileConfigReposByDependentRepo(
        IEnumerable<WorkspaceFileVersionConfig> configs,
        IReadOnlyDictionary<string, int> nameToRepoId)
    {
        var result = new Dictionary<int, HashSet<string>>();
        foreach (var cfg in configs)
        {
            if (cfg.File?.IsMissingOnDisk == true) continue;
            var dependentRepoId = cfg.File!.RepositoryId;
            foreach (var token in ExtractTokens(cfg.VersionPattern))
            {
                if (!nameToRepoId.TryGetValue(token.RepositoryName, out var referencedRepoId)) continue;
                if (referencedRepoId == dependentRepoId) continue;
                if (!result.TryGetValue(dependentRepoId, out var set))
                    result[dependentRepoId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Deduplicate by repository name, not full token key (branch/commit are the same edge).
                set.Add(token.RepositoryName);
            }
        }

        return result;
    }

    /// <summary>Returns the set of RepositoryIds whose own file-config version pattern(s) include a token referencing that same repo (self-stamping).</summary>
    private static HashSet<int> BuildSelfReferencingRepoIds(
        IEnumerable<WorkspaceFileVersionConfig> configs,
        IReadOnlyDictionary<string, int> nameToRepoId)
    {
        var result = new HashSet<int>();
        foreach (var cfg in configs)
        {
            if (cfg.File?.IsMissingOnDisk == true) continue;
            var dependentRepoId = cfg.File!.RepositoryId;
            foreach (var token in ExtractTokens(cfg.VersionPattern))
            {
                if (nameToRepoId.TryGetValue(token.RepositoryName, out var referencedRepoId) && referencedRepoId == dependentRepoId)
                    result.Add(dependentRepoId);
            }
        }
        return result;
    }

    /// <summary>Returns out-of-date file-config token rows for the workspace scoped to <paramref name="contextId"/>, grouped by dependent RepositoryId. <see cref="WorkspaceFileLineStatus"/> rows carry their own <c>WorkspaceFeatureContextId</c> (unlike <see cref="WorkspaceFile.IsMissingOnDisk"/>), so this is a plain filter rather than a context-state overlay.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<(string FileName, string TokenName, string CurrentValue, string ExpectedValue)>>> GetMismatchedFileVersionLinesByRepoAsync(
        int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.WorkspaceFileLineStatuses
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.WorkspaceFeatureContextId == contextId.Value && s.TokenName != "")
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(s => s.RepositoryId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<(string, string, string, string)>)g
                    .Select(s => (s.FileName, s.TokenName, s.CurrentValue ?? "", s.ExpectedValue ?? ""))
                    .ToList());
    }

    /// <summary>Out-of-date file-config token rows for a single repository scoped to <paramref name="contextId"/> (badge tooltip).</summary>
    public async Task<IReadOnlyList<(string FileName, string TokenName, string CurrentValue, string ExpectedValue)>> GetMismatchedFileVersionLinesForRepoAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.WorkspaceFileLineStatuses
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.WorkspaceFeatureContextId == contextId.Value && s.RepositoryId == repositoryId && s.TokenName != "")
            .ToListAsync(cancellationToken);
        return rows
            .Select(s => (s.FileName, s.TokenName, s.CurrentValue ?? "", s.ExpectedValue ?? ""))
            .ToList();
    }

    /// <summary>Returns out-of-date file line statuses for the workspace scoped to <paramref name="contextId"/>, grouped by RepositoryId.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<WorkspaceFileLineStatus>>> GetFileLineStatusByWorkspaceAsync(
        int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.WorkspaceFileLineStatuses
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.WorkspaceFeatureContextId == contextId.Value)
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(s => s.RepositoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<WorkspaceFileLineStatus>)g.ToList());
    }

    /// <summary>Out-of-date file line statuses for a single repository scoped to <paramref name="contextId"/>.</summary>
    public async Task<IReadOnlyList<WorkspaceFileLineStatus>> GetFileLineStatusForRepoAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.WorkspaceFileLineStatuses
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.WorkspaceFeatureContextId == contextId.Value && s.RepositoryId == repositoryId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Returns per-repo (FileName, TokenName, Version) triples for the OK badge tooltip.
    /// Each entry represents a tracked token in a version file whose expected value equals the current workspace GitVersion.
    /// Only repos present in <paramref name="repoVersionMap"/> contribute entries.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<(string FileName, string TokenName, string Version)>>> GetAllFileVersionLinesByRepoAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyDictionary<string, string> repoVersionMap,
        CancellationToken cancellationToken = default)
    {
        var configs = await versionConfigRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        ApplyMissingFlagOverlay(configs, await GetMissingFlagsByFileIdAsync(workspaceId, contextId, cancellationToken));
        var result = new Dictionary<int, List<(string FileName, string TokenName, string Version)>>();

        foreach (var cfg in configs)
        {
            if (cfg.File?.Repository == null || cfg.File.IsMissingOnDisk == true) continue;
            var repoId = cfg.File.RepositoryId;
            var fileName = cfg.File.FileName;
            var tokens = ExtractTokens(cfg.VersionPattern);

            foreach (var token in tokens)
            {
                if (token.Kind != FileVersionTokenKind.GitVersion) continue;
                if (!repoVersionMap.TryGetValue(token.RepositoryName, out var ver) || string.IsNullOrEmpty(ver)) continue;
                if (!result.TryGetValue(repoId, out var list))
                    result[repoId] = list = [];
                if (!list.Any(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(e.TokenName, token.TokenKey, StringComparison.OrdinalIgnoreCase)))
                    list.Add((fileName, token.TokenKey, ver));
            }
        }

        return result.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<(string, string, string)>)kvp.Value);
    }

    /// <summary>OK-badge file version lines for a single repository.</summary>
    public async Task<IReadOnlyList<(string FileName, string TokenName, string Version)>> GetAllFileVersionLinesForRepoAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        IReadOnlyDictionary<string, string> repoVersionMap,
        CancellationToken cancellationToken = default)
    {
        var configs = await versionConfigRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        ApplyMissingFlagOverlay(configs, await GetMissingFlagsByFileIdAsync(workspaceId, contextId, cancellationToken));
        var list = new List<(string FileName, string TokenName, string Version)>();

        foreach (var cfg in configs)
        {
            if (cfg.File?.Repository == null || cfg.File.IsMissingOnDisk == true) continue;
            if (cfg.File.RepositoryId != repositoryId) continue;
            var fileName = cfg.File.FileName;
            var tokens = ExtractTokens(cfg.VersionPattern);

            foreach (var token in tokens)
            {
                if (token.Kind != FileVersionTokenKind.GitVersion) continue;
                if (!repoVersionMap.TryGetValue(token.RepositoryName, out var ver) || string.IsNullOrEmpty(ver)) continue;
                if (!list.Any(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(e.TokenName, token.TokenKey, StringComparison.OrdinalIgnoreCase)))
                    list.Add((fileName, token.TokenKey, ver));
            }
        }

        return list;
    }

    /// <summary>
    /// Checks which pattern lines from <paramref name="pattern"/> cannot be matched in the actual file on disk.
    /// Used by the version config dialog to highlight pattern lines that no longer exist in the file.
    /// Returns token names (repo names) whose pattern line was not found. Returns empty if the agent is
    /// not connected, the file is missing, or the call fails.
    /// </summary>
    public async Task<IReadOnlyList<string>> ValidatePatternAgainstFileAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        string? repositoryName,
        string? filePath,
        string? pattern,
        CancellationToken cancellationToken = default)
    {
        if (!agentBridge.IsAgentConnected) return [];
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(repositoryName) || string.IsNullOrWhiteSpace(filePath))
            return [];

        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return [];

        try
        {
            var (workspaceRoot, workspaceFolderName) = await pathResolver.GetAgentWorkspaceArgsAsync(contextId, cancellationToken);
            var resp = await agentBridge.SendCommandAsync("CheckFileVersions", new
            {
                workspaceName = workspaceFolderName,
                workspaceRoot,
                files = new[]
                {
                    new
                    {
                        repositoryName,
                        filePath,
                        pattern,
                        expectedValues = new Dictionary<string, string>()
                    }
                }
            }, cancellationToken);

            if (!resp.Success || resp.Data == null) return [];

            var result = AgentResponseJson.DeserializeAgentResponse<CheckFileVersionsAgentResponse>(resp.Data);
            var fileResult = result?.Files?.FirstOrDefault();
            if (fileResult == null || fileResult.FileMissing) return [];

            return (IReadOnlyList<string>)(fileResult.NotMatchedTokens ?? []);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ValidatePatternAgainstFile failed for {FilePath}", filePath);
            return [];
        }
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
        [System.Text.Json.Serialization.JsonPropertyName("fileMissing")] public bool FileMissing { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("outOfDateLines")] public List<CheckFileVersionsAgentOutOfDateLine>? OutOfDateLines { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("notMatchedTokens")] public List<string>? NotMatchedTokens { get; set; }
    }

    private sealed class CheckFileVersionsAgentOutOfDateLine
    {
        [System.Text.Json.Serialization.JsonPropertyName("tokenName")] public string? TokenName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("currentValue")] public string? CurrentValue { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("expectedValue")] public string? ExpectedValue { get; set; }
    }
}
