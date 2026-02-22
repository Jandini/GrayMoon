using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

public sealed class WorkspaceFileVersionService(
    IAgentBridge agentBridge,
    WorkspaceRepository workspaceRepository,
    WorkspaceFileVersionConfigRepository versionConfigRepository,
    ILogger<WorkspaceFileVersionService> logger)
{
    /// <summary>
    /// For every file in the workspace that has a version pattern configured:
    ///   1. Resolves the current version for each repo token referenced in patterns via GetRepositoryVersion.
    ///   2. Calls UpdateFileVersions on the agent to perform the in-place substitution.
    /// Returns (updatedLineCount, failedFileCount, fatalError).
    /// </summary>
    public async Task<(int Updated, int Failed, string? Error)> UpdateAllVersionsAsync(
        int workspaceId, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return (0, 0, "Workspace not found.");
        if (!agentBridge.IsAgentConnected) return (0, 0, "Agent is not connected.");

        var configs = await versionConfigRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (configs.Count == 0) return (0, 0, "No version configurations found. Use Configure on a file first.");

        // Collect all unique repo-name tokens used across all patterns
        var repoNamesInUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in configs)
        {
            foreach (var token in ExtractTokens(cfg.VersionPattern))
                repoNamesInUse.Add(token);
        }

        // Resolve current version for each referenced repo
        var repoVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repoName in repoNamesInUse)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resp = await agentBridge.SendCommandAsync("GetRepositoryVersion", new
            {
                workspaceName = workspace.Name,
                repositoryName = repoName
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

            try
            {
                var resp = await agentBridge.SendCommandAsync("UpdateFileVersions", new
                {
                    workspaceName = workspace.Name,
                    repositoryName = file.Repository.RepositoryName,
                    filePath = file.FilePath,
                    versionPattern = cfg.VersionPattern,
                    repoVersions
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
}
