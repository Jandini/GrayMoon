using GrayMoon.App.Data;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Features;

public sealed class WorkspaceContextPathResolver(
    IDbContextFactory<AppDbContext> dbContextFactory,
    WorkspaceService workspaceService,
    IWorkspaceFeatureContextResolver contextResolver) : IWorkspaceContextPathResolver
{
    public async Task<string> GetContextRootAsync(WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
    {
        var info = await contextResolver.GetRequiredAsync(contextId, cancellationToken: cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == info.WorkspaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace {info.WorkspaceId} was not found.");

        if (info.IsSpecialWorkspace)
            return await ResolveSpecialWorkspaceFolderAsync(workspace.Name, workspace, cancellationToken);

        var featureName = info.FeatureName
            ?? throw new InvalidOperationException($"Feature context {contextId.Value} has no Feature name.");

        // Prefer persisted root unless it is a drive-root .graymoon path (legacy bug: C:\.graymoon\...).
        // Persisted roots stay put when the global Feature storage setting changes.
        if (!string.IsNullOrWhiteSpace(workspace.ManagedFeatureStorageRoot)
            && !AgentPath.IsLegacyWindowsDriveRootGraymoonPath(workspace.ManagedFeatureStorageRoot))
        {
            return AgentPath.Combine(workspace.ManagedFeatureStorageRoot, featureName);
        }

        var storageRoot = await workspaceService.ResolveFeatureStorageRootPathAsync(
            persistIfMissing: false,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new InvalidOperationException(
                "Feature storage root is not configured. Set it on the Settings page (or connect the Agent so the host user profile can be used as the default).");

        var derivedFeaturesRoot = AgentPath.Combine(storageRoot, workspace.Name, "features");
        return AgentPath.Combine(derivedFeaturesRoot, featureName);
    }

    public async Task<string> GetRepositoryPathAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        CancellationToken cancellationToken = default)
    {
        var info = await contextResolver.GetRequiredAsync(contextId, cancellationToken: cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var link = await db.WorkspaceRepositories
            .AsNoTracking()
            .Include(l => l.Repository)
            .FirstOrDefaultAsync(
                l => l.WorkspaceRepositoryId == workspaceRepositoryId && l.WorkspaceId == info.WorkspaceId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"WorkspaceRepository {workspaceRepositoryId} was not found in workspace {info.WorkspaceId}.");

        var repoName = link.Repository?.RepositoryName
            ?? throw new InvalidOperationException($"Repository name missing for WorkspaceRepository {workspaceRepositoryId}.");

        if (!info.IsSpecialWorkspace)
        {
            var featureRepo = await db.WorkspaceFeatureRepositories
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.WorkspaceFeatureContextId == contextId.Value && r.WorkspaceRepositoryId == workspaceRepositoryId,
                    cancellationToken);

            if (featureRepo is not null && !string.IsNullOrWhiteSpace(featureRepo.WorktreePath))
            {
                // A Windows Agent/git may persist worktree paths with forward slashes (e.g.
                // C:/Users/...); AgentPath.Normalize fixes that to '\' while leaving an
                // already-POSIX path (a Linux/macOS Worker) untouched.
                return AgentPath.Normalize(featureRepo.WorktreePath);
            }
        }

        var contextRoot = await GetContextRootAsync(contextId, cancellationToken);
        return AgentPath.Combine(contextRoot, repoName);
    }

    public async Task<(string AgentWorkspaceRoot, string AgentWorkspaceFolderName)> GetAgentWorkspaceArgsAsync(
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        var contextRoot = await GetContextRootAsync(contextId, cancellationToken);
        // The context root is Agent/Worker-host-shaped (Windows or POSIX), which may differ from
        // the App's own OS (e.g. App in Linux Docker, Worker on Windows) - never use host Path.*.
        var folderName = AgentPath.GetFileName(contextRoot);
        if (string.IsNullOrWhiteSpace(folderName))
            throw new InvalidOperationException($"Cannot derive agent folder name from context root '{contextRoot}'.");

        var parent = AgentPath.GetDirectoryName(contextRoot);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException($"Cannot derive agent parent root from context root '{contextRoot}'.");

        return (parent, folderName);
    }

    private async Task<string> ResolveSpecialWorkspaceFolderAsync(
        string workspaceName,
        Models.Workspace workspace,
        CancellationToken cancellationToken)
    {
        var configuredRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        if (string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException($"Workspace root is not configured for workspace {workspaceName}.");

        var folder = workspaceService.GetWorkspacePath(workspaceName, configuredRoot);
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException($"Workspace folder path is empty for workspace {workspaceName}.");

        return folder.TrimEnd('\\', '/');
    }
}
