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
            && !WorkerPath.IsLegacyWindowsDriveRootGraymoonPath(workspace.ManagedFeatureStorageRoot))
        {
            return WorkerPath.Combine(workspace.ManagedFeatureStorageRoot, featureName);
        }

        var storageRoot = await workspaceService.ResolveFeatureStorageRootPathAsync(
            persistIfMissing: false,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new InvalidOperationException(
                "Feature storage root is not configured. Set it on the Settings page (or connect the Worker so the host user profile can be used as the default).");

        var derivedFeaturesRoot = WorkerPath.Combine(storageRoot, workspace.Name, "features");
        return WorkerPath.Combine(derivedFeaturesRoot, featureName);
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
                // A Windows Worker/git may persist worktree paths with forward slashes (e.g.
                // C:/Users/...); WorkerPath.Normalize fixes that to '\' while leaving an
                // already-POSIX path (a Linux/macOS Worker) untouched.
                return WorkerPath.Normalize(featureRepo.WorktreePath);
            }
        }

        var contextRoot = await GetContextRootAsync(contextId, cancellationToken);
        return WorkerPath.Combine(contextRoot, repoName);
    }

    public async Task<(string WorkerWorkspaceRoot, string WorkerWorkspaceFolderName)> GetWorkerWorkspaceArgsAsync(
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        var contextRoot = await GetContextRootAsync(contextId, cancellationToken);
        // The context root is Worker-host-shaped (Windows or POSIX), which may differ from
        // the App's own OS (e.g. App in Linux Docker, Worker on Windows) - never use host Path.*.
        var folderName = WorkerPath.GetFileName(contextRoot);
        if (string.IsNullOrWhiteSpace(folderName))
            throw new InvalidOperationException($"Cannot derive worker folder name from context root '{contextRoot}'.");

        var parent = WorkerPath.GetDirectoryName(contextRoot);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException($"Cannot derive worker parent root from context root '{contextRoot}'.");

        return (parent, folderName);
    }

    public async Task<WorkerWorkspaceArgs> GetWorkerArgsAsync(
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        var (root, folder) = await GetWorkerWorkspaceArgsAsync(contextId, cancellationToken);
        return new WorkerWorkspaceArgs(root, folder, null);
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
