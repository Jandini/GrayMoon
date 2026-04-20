using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

public sealed class WorkspaceFileVersionService(
    IAgentBridge agentBridge,
    WorkspaceService workspaceService,
    WorkspaceRepository workspaceRepository,
    WorkspaceFileVersionConfigRepository versionConfigRepository,
    ILogger<WorkspaceFileVersionService> logger)
{
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
}
