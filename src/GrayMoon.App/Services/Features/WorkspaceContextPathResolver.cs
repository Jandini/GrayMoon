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

        if (!string.IsNullOrWhiteSpace(workspace.ManagedFeatureStorageRoot))
        {
            var featureName = info.FeatureName
                ?? throw new InvalidOperationException($"Feature context {contextId.Value} has no Feature name.");
            return CombineWindows(workspace.ManagedFeatureStorageRoot.TrimEnd('\\', '/'), featureName);
        }

        var workspaceFolder = await ResolveSpecialWorkspaceFolderAsync(workspace.Name, workspace, cancellationToken);
        var parent = GetWindowsDirectoryName(workspaceFolder)
            ?? throw new InvalidOperationException("Cannot derive Feature storage parent from workspace root.");
        // Mirror EnsureManagedFeatureStorageRoot: never place .graymoon under a drive root (e.g. C:\).
        if (IsWindowsDriveRoot(parent))
            parent = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var derivedFeaturesRoot = CombineWindows(parent, ".graymoon", workspace.Name, "features");
        var feature = info.FeatureName
            ?? throw new InvalidOperationException($"Feature context {contextId.Value} has no Feature name.");
        return CombineWindows(derivedFeaturesRoot, feature);
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
                return featureRepo.WorktreePath.TrimEnd('\\', '/');
        }

        var contextRoot = await GetContextRootAsync(contextId, cancellationToken);
        return CombineWindows(contextRoot, repoName);
    }

    public async Task<(string AgentWorkspaceRoot, string AgentWorkspaceFolderName)> GetAgentWorkspaceArgsAsync(
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        var contextRoot = await GetContextRootAsync(contextId, cancellationToken);
        // Agent paths are Windows-shaped even when the App (and CI) run on Linux - do not use host Path.*.
        var folderName = GetWindowsFileName(contextRoot);
        if (string.IsNullOrWhiteSpace(folderName))
            throw new InvalidOperationException($"Cannot derive agent folder name from context root '{contextRoot}'.");

        var parent = GetWindowsDirectoryName(contextRoot);
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

    private static string CombineWindows(params string[] parts)
    {
        var cleaned = parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('/', '\\').Trim('\\'))
            .ToArray();
        return string.Join('\\', cleaned);
    }

    private static string GetWindowsFileName(string path)
    {
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        if (string.IsNullOrEmpty(normalized))
            return string.Empty;
        var last = normalized.LastIndexOf('\\');
        return last >= 0 ? normalized[(last + 1)..] : normalized;
    }

    private static string? GetWindowsDirectoryName(string path)
    {
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        if (string.IsNullOrEmpty(normalized))
            return null;
        var last = normalized.LastIndexOf('\\');
        return last >= 0 ? normalized[..last] : null;
    }

    /// <summary>True for Windows drive roots such as <c>C:</c> / <c>C:\</c> (after trim).</summary>
    private static bool IsWindowsDriveRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        return normalized.Length == 2 && char.IsLetter(normalized[0]) && normalized[1] == ':';
    }
}
