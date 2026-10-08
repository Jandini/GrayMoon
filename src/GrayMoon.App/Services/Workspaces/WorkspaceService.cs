using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Features;

namespace GrayMoon.App.Services.Workspaces;

public sealed class WorkspaceService(IWorkerBridge workerBridge, ILogger<WorkspaceService> logger, AppSettingRepository appSettingRepository, Microsoft.Extensions.Options.IOptions<WorkspaceOptions> workspaceOptions)
{
    private string? _cachedRootPath;
    private string? _cachedFeatureStorageRootPath;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public string? RootPath => _cachedRootPath;

    public async Task<string?> GetRootPathAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedRootPath != null)
            return _cachedRootPath;

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedRootPath != null)
                return _cachedRootPath;

            var dbOverride = await appSettingRepository.GetValueAsync(AppSettingRepository.WorkspaceRootPathKey);
            if (!string.IsNullOrWhiteSpace(dbOverride))
            {
                _cachedRootPath = dbOverride.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                logger.LogInformation("Using configured workspace root: {RootPath}", _cachedRootPath);
                return _cachedRootPath;
            }

            logger.LogWarning("No workspace root configured. Set one on the Settings page.");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error reading workspace root from settings");
            return null;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>Returns the workspace root for a specific workspace, falling back to the global configured root.</summary>
    public async Task<string?> GetRootPathForWorkspaceAsync(Workspace? workspace, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(workspace?.RootPath))
            return workspace.RootPath;
        return await GetRootPathAsync(cancellationToken);
    }

    public string GetWorkspacePath(string workspaceName, string? rootOverride = null)
    {
        var root = !string.IsNullOrWhiteSpace(rootOverride) ? rootOverride : RootPath;
        if (string.IsNullOrEmpty(root))
            return string.Empty;
        var safeName = SanitizeDirectoryName(workspaceName);
        return Path.Combine(root, safeName);
    }

    public async Task<string?> GetWorkspacePathAsync(string workspaceName, string? rootOverride = null, CancellationToken cancellationToken = default)
    {
        var root = !string.IsNullOrWhiteSpace(rootOverride) ? rootOverride : await GetRootPathAsync(cancellationToken);
        if (string.IsNullOrEmpty(root))
            return null;
        var safeName = SanitizeDirectoryName(workspaceName);
        return Path.Combine(root, safeName);
    }

    public async Task<bool> DirectoryExistsAsync(string workspaceName, string? rootOverride = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return false;

        var root = !string.IsNullOrWhiteSpace(rootOverride) ? rootOverride : await GetRootPathAsync(cancellationToken);
        var response = await workerBridge.SendCommandAsync("GetWorkspaceExists", new { workspaceName, workspaceRoot = root }, cancellationToken);
        if (!response.Success || response.Data == null)
            return false;

        var data = WorkerResponseJson.DeserializeWorkerResponse<WorkerWorkspaceExistsResponse>(response.Data);
        return data?.Exists ?? false;
    }

    /// <summary>
    /// Whether the Workspace folder exists and, if so, whether it is empty. <c>Error</c> is set when the Worker could
    /// not answer; <c>IsEmpty</c> is null when the folder does not exist.
    /// </summary>
    public async Task<WorkspaceDirectoryState> GetDirectoryStateAsync(string workspaceName, string? rootOverride = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return new WorkspaceDirectoryState(false, null, null);

        var root = !string.IsNullOrWhiteSpace(rootOverride) ? rootOverride : await GetRootPathAsync(cancellationToken);
        var response = await workerBridge.SendCommandAsync("GetWorkspaceExists", new { workspaceName, workspaceRoot = root }, cancellationToken);
        if (!response.Success)
            return new WorkspaceDirectoryState(false, null, response.Error ?? "The Worker could not check the Workspace folder.");

        var data = WorkerResponseJson.DeserializeWorkerResponse<WorkerWorkspaceExistsResponse>(response.Data);
        if (data is null)
            return new WorkspaceDirectoryState(false, null, "The Worker returned an unreadable response for the Workspace folder.");

        return new WorkspaceDirectoryState(data.Exists, data.Exists ? data.IsEmpty : null, null);
    }

    public async Task<int> GetRepositoryCountAsync(string workspaceName, string? rootOverride = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return 0;

        var root = !string.IsNullOrWhiteSpace(rootOverride) ? rootOverride : await GetRootPathAsync(cancellationToken);
        var maxParallel = Math.Max(1, workspaceOptions.Value.MaxParallelOperations);
        var response = await workerBridge.SendCommandAsync("GetWorkspaceRepositories", new { workspaceName, workspaceRoot = root, maxParallelOperations = maxParallel }, cancellationToken);
        if (!response.Success || response.Data == null)
            return 0;

        var data = WorkerResponseJson.DeserializeWorkerResponse<WorkerRepositoriesListResponse>(response.Data);
        return data?.Repositories?.Count ?? 0;
    }

    public async Task<IReadOnlyList<(string Name, string? OriginUrl)>> GetWorkspaceRepositoryInfosAsync(
        string workspaceName,
        string? rootOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return Array.Empty<(string, string?)>();

        var root = !string.IsNullOrWhiteSpace(rootOverride) ? rootOverride : await GetRootPathAsync(cancellationToken);
        var maxParallel = Math.Max(1, workspaceOptions.Value.MaxParallelOperations);
        var response = await workerBridge.SendCommandAsync("GetWorkspaceRepositories", new { workspaceName, workspaceRoot = root, maxParallelOperations = maxParallel }, cancellationToken);
        if (!response.Success || response.Data == null)
            return Array.Empty<(string, string?)>();

        var data = WorkerResponseJson.DeserializeWorkerResponse<WorkerWorkspaceRepositoriesResponse>(response.Data);
        var infos = data?.RepositoryInfos;
        if (infos == null)
            return Array.Empty<(string, string?)>();

        var list = new List<(string Name, string? OriginUrl)>();
        foreach (var el in infos)
        {
            if (string.IsNullOrWhiteSpace(el.Name)) continue;
            list.Add((el.Name, el.OriginUrl));
        }
        return list;
    }

    public async Task CreateDirectoryAsync(string workspaceName, string? rootOverride = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return;

        var root = !string.IsNullOrWhiteSpace(rootOverride) ? rootOverride : await GetRootPathAsync(cancellationToken);
        await workerBridge.SendCommandAsync("EnsureWorkspace", new { workspaceName, workspaceRoot = root }, cancellationToken);
        logger.LogInformation("Created workspace directory: {Name}", workspaceName);
    }

    /// <summary>Refreshes the cached workspace root from DB settings. Call this when settings change or on startup.</summary>
    public async Task RefreshRootPathAsync(CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            var dbOverride = await appSettingRepository.GetValueAsync(AppSettingRepository.WorkspaceRootPathKey);
            if (!string.IsNullOrWhiteSpace(dbOverride))
            {
                _cachedRootPath = dbOverride.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                logger.LogInformation("Using configured workspace root: {RootPath}", _cachedRootPath);
            }
            else
            {
                _cachedRootPath = null;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error refreshing workspace root from settings");
            _cachedRootPath = null;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>Clears the cached workspace root.</summary>
    public void ClearCachedRootPath()
    {
        _cachedRootPath = null;
    }

    /// <summary>Clears the cached Feature storage root.</summary>
    public void ClearCachedFeatureStorageRootPath()
    {
        _cachedFeatureStorageRootPath = null;
    }

    /// <summary>
    /// Returns the configured Feature worktree storage root (e.g. C:\Users\name\.graymoon), or null if unset.
    /// Does not ask the Worker.
    /// </summary>
    public async Task<string?> GetFeatureStorageRootPathAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedFeatureStorageRootPath != null)
            return _cachedFeatureStorageRootPath;

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedFeatureStorageRootPath != null)
                return _cachedFeatureStorageRootPath;

            var configured = await appSettingRepository.GetValueAsync(AppSettingRepository.FeatureStorageRootPathKey);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                _cachedFeatureStorageRootPath = WorkerPath.Normalize(configured);
                logger.LogInformation("Using configured Feature storage root: {RootPath}", _cachedFeatureStorageRootPath);
                return _cachedFeatureStorageRootPath;
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error reading Feature storage root from settings");
            return null;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Resolves Feature storage root from settings, or from the Worker host user profile
    /// (<c>{userProfile}\.graymoon</c>). When <paramref name="persistIfMissing"/> is true and the
    /// setting was empty, persists the Worker default so Settings and future Features share it.
    /// Does not relocate existing Workspace.ManagedFeatureStorageRoot values.
    /// </summary>
    public async Task<string?> ResolveFeatureStorageRootPathAsync(
        bool persistIfMissing = false,
        CancellationToken cancellationToken = default)
    {
        var configured = await GetFeatureStorageRootPathAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var fromWorker = await TryGetWorkerDefaultFeatureStorageRootAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(fromWorker))
            return null;

        if (persistIfMissing)
        {
            await appSettingRepository.SetValueAsync(AppSettingRepository.FeatureStorageRootPathKey, fromWorker);
            ClearCachedFeatureStorageRootPath();
            logger.LogInformation("Persisted Worker-default Feature storage root: {RootPath}", fromWorker);
            return await GetFeatureStorageRootPathAsync(cancellationToken);
        }

        return fromWorker;
    }

    /// <summary>
    /// Asks the Worker for the host user profile and returns <c>{profile}/.graymoon</c> (or
    /// <c>{profile}\.graymoon</c> on a Windows Worker), or null if unavailable. The join style
    /// follows whatever shape the Worker's own <c>UserProfilePath</c> already has - a Linux/macOS
    /// Worker's profile already starts with '/', so no separate "which OS" signal is needed.
    /// </summary>
    public async Task<string?> TryGetWorkerDefaultFeatureStorageRootAsync(CancellationToken cancellationToken = default)
    {
        if (!workerBridge.IsWorkerConnected)
            return null;

        try
        {
            var response = await workerBridge.SendCommandAsync("GetHostInfo", new { }, cancellationToken);
            if (!response.Success || response.Data == null)
                return null;

            var data = WorkerResponseJson.DeserializeWorkerResponse<GetHostInfoWorkerResponse>(response.Data);
            var profile = data?.UserProfilePath?.Trim();
            if (string.IsNullOrWhiteSpace(profile))
                return null;

            var normalizedProfile = WorkerPath.Normalize(profile);
            return string.IsNullOrWhiteSpace(normalizedProfile)
                ? null
                : WorkerPath.Combine(normalizedProfile, ".graymoon");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve Worker user profile for Feature storage default");
            return null;
        }
    }

    /// <summary>Asks the worker to validate whether the given path is usable as a workspace root.</summary>
    public async Task<(bool IsValid, string? ErrorMessage)> ValidatePathAsync(string path, CancellationToken cancellationToken = default)
    {
        var response = await workerBridge.SendCommandAsync("ValidatePath", new { path }, cancellationToken);
        if (!response.Success)
            return (false, response.Error ?? "Worker did not respond.");

        var data = WorkerResponseJson.DeserializeWorkerResponse<ValidatePathWorkerResponse>(response.Data);
        return (data?.IsValid ?? false, data?.ErrorMessage);
    }

    private static string SanitizeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "workspace";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Trim().Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "workspace" : sanitized;
    }
}

/// <summary>Workspace folder state as seen by the Worker (see <see cref="WorkspaceService.GetDirectoryStateAsync"/>).</summary>
public sealed record WorkspaceDirectoryState(bool Exists, bool? IsEmpty, string? Error);
