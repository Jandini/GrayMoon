using System.Collections.Concurrent;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Exceptions;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Git;

public sealed partial class WorkspaceGitService
{
    /// <summary>
    /// Fires <c>dotnet restore --force --no-cache &lt;project.csproj&gt;</c> for each specified project file.
    /// Best-effort: errors are logged and swallowed so the caller's workflow is never interrupted.
    /// </summary>
    public async Task RestoreDependenciesAsync(int workspaceId, IEnumerable<(string RepoName, IReadOnlyList<string> ProjectPaths)> repos, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return;
        var workspaceRoot = await _workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var tasks = repos
            .Where(r => r.ProjectPaths.Count > 0)
            .Select(async r =>
            {
                try
                {
                    await _agentBridge.SendCommandAsync(
                        "DotnetRestore",
                        new { workspaceName = workspace.Name, repositoryName = r.RepoName, projectPaths = r.ProjectPaths, workspaceRoot },
                        cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "dotnet restore failed for {RepoName} in workspace {WorkspaceName}, continuing", r.RepoName, workspace.Name);
                }
            });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Fires <c>dotnet restore --force --no-cache</c> for all tracked project files across all workspace
    /// repositories, skipping repos pinned to a tag. Best-effort: individual restore errors are logged and swallowed.
    /// Returns the total number of project files targeted for restore.
    /// </summary>
    public async Task<int> RestoreAllWorkspacePackagesAsync(
        int workspaceId,
        Action<string> setProgress,
        CancellationToken cancellationToken)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new AgentNotConnectedException();

        setProgress("Restoring packages...");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return 0;

        var tagPinnedIds = workspace.Repositories
            .Where(l => !string.IsNullOrWhiteSpace(l.CheckedOutTag))
            .Select(l => l.RepositoryId)
            .ToHashSet();

        var projects = await _workspaceProjectRepository.GetByWorkspaceIdAsync(workspaceId);
        var repoGroups = projects
            .Where(p => p.Repository != null
                        && !string.IsNullOrWhiteSpace(p.ProjectFilePath)
                        && !tagPinnedIds.Contains(p.RepositoryId))
            .GroupBy(p => (p.RepositoryId, RepoName: p.Repository!.RepositoryName))
            .Select(g => (g.Key.RepoName, ProjectPaths: (IReadOnlyList<string>)g.Select(p => p.ProjectFilePath!).ToList()))
            .ToList();

        var totalCount = repoGroups.Sum(r => r.ProjectPaths.Count);
        if (totalCount == 0)
            return 0;

        await RestoreDependenciesAsync(workspaceId, repoGroups, cancellationToken);
        return totalCount;
    }

    public async Task<int> RestoreSyncedWorkspacePackagesAsync(
        int workspaceId,
        IReadOnlySet<int> syncedRepoIds,
        Action<string> setProgress,
        CancellationToken cancellationToken)
    {
        if (!_agentBridge.IsAgentConnected)
            throw new AgentNotConnectedException();

        if (syncedRepoIds.Count == 0)
            return 0;

        setProgress("Restoring packages...");

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return 0;

        var tagPinnedIds = workspace.Repositories
            .Where(l => !string.IsNullOrWhiteSpace(l.CheckedOutTag))
            .Select(l => l.RepositoryId)
            .ToHashSet();

        var projects = await _workspaceProjectRepository.GetByWorkspaceIdAsync(workspaceId);
        var repoGroups = projects
            .Where(p => p.Repository != null
                        && !string.IsNullOrWhiteSpace(p.ProjectFilePath)
                        && syncedRepoIds.Contains(p.RepositoryId)
                        && !tagPinnedIds.Contains(p.RepositoryId))
            .GroupBy(p => (p.RepositoryId, RepoName: p.Repository!.RepositoryName))
            .Select(g => (g.Key.RepoName, ProjectPaths: (IReadOnlyList<string>)g.Select(p => p.ProjectFilePath!).ToList()))
            .ToList();

        var totalCount = repoGroups.Sum(r => r.ProjectPaths.Count);
        if (totalCount == 0)
            return 0;

        await RestoreDependenciesAsync(workspaceId, repoGroups, cancellationToken);
        return totalCount;
    }
}
