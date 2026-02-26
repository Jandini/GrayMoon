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
    ///   1. Resolves the current version for each repo token referenced in patterns via GetRepositoryVersion.
    ///   2. Calls UpdateFileVersions on the agent to perform the in-place substitution.
    /// When <paramref name="selectedRepositoryIds"/> is set, only repositories in that set are included: pattern lines are filtered to tokens matching selected repo names, and only files in selected repos are updated.
    /// Returns (updatedLineCount, failedFileCount, fatalError).
    /// </summary>
    public async Task<(int Updated, int Failed, string? Error)> UpdateAllVersionsAsync(
        int workspaceId,
        IReadOnlySet<int>? selectedRepositoryIds = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return (0, 0, "Workspace not found.");
        if (!agentBridge.IsAgentConnected) return (0, 0, "Agent is not connected.");

        var configs = await versionConfigRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (configs.Count == 0) return (0, 0, "No version configurations found. Use Configure on a file first.");

        HashSet<string>? selectedRepoNames = null;
        if (selectedRepositoryIds != null && selectedRepositoryIds.Count > 0)
        {
            selectedRepoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in workspace.Repositories)
            {
                if (link.RepositoryId != 0 && selectedRepositoryIds.Contains(link.RepositoryId) && !string.IsNullOrEmpty(link.Repository?.RepositoryName))
                    selectedRepoNames.Add(link.Repository.RepositoryName);
            }
            if (selectedRepoNames.Count == 0) return (0, 0, "No selected repositories.");
        }

        // Collect all unique repo-name tokens used across all patterns
        var repoNamesInUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in configs)
        {
            foreach (var token in ExtractTokens(cfg.VersionPattern))
                repoNamesInUse.Add(token);
        }
        if (selectedRepoNames != null)
            repoNamesInUse.IntersectWith(selectedRepoNames);

        // Resolve current version for each referenced repo
        var repoVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repoName in repoNamesInUse)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
            var resp = await agentBridge.SendCommandAsync("GetRepositoryVersion", new
            {
                workspaceName = workspace.Name,
                repositoryName = repoName,
                workspaceRoot
            }, cancellationToken);

            if (resp.Success && resp.Data != null)
            {
                var vr = AgentResponseJson.DeserializeAgentResponse<AgentGetRepositoryVersionResponse>(resp.Data);
                if (vr?.Version != null)
                    repoVersions[repoName] = vr.Version;
                else
                    logger.LogWarning("Could not resolve version for repo {RepoName}", repoName);
            }
            else
            {
                logger.LogWarning("GetRepositoryVersion failed for {RepoName}: {Error}", repoName, resp.Error);
            }
        }

        // Update each configured file
        var totalUpdated = 0;
        var totalFailed = 0;

        foreach (var cfg in configs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = cfg.File;
            if (file?.Repository == null) continue;

            if (selectedRepositoryIds != null && selectedRepositoryIds.Count > 0 && !selectedRepositoryIds.Contains(file.RepositoryId))
                continue;

            var versionPatternToSend = cfg.VersionPattern;
            if (selectedRepoNames != null)
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
                    totalUpdated += result?.UpdatedCount ?? 0;
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

        return (totalUpdated, totalFailed, null);
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
