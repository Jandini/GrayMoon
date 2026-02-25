using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

public class WorkspaceService(IAgentBridge agentBridge, ILogger<WorkspaceService> logger, AppSettingRepository appSettingRepository)
{
    private string? _cachedRootPath;
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

    public string GetWorkspacePath(string workspaceName)
    {
        var root = RootPath;
        if (string.IsNullOrEmpty(root))
            return string.Empty;
        var safeName = SanitizeDirectoryName(workspaceName);
        return Path.Combine(root, safeName);
    }

    public async Task<string?> GetWorkspacePathAsync(string workspaceName, CancellationToken cancellationToken = default)
    {
        var root = await GetRootPathAsync(cancellationToken);
        if (string.IsNullOrEmpty(root))
            return null;
        var safeName = SanitizeDirectoryName(workspaceName);
        return Path.Combine(root, safeName);
    }

    public async Task<bool> DirectoryExistsAsync(string workspaceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return false;

        var root = await GetRootPathAsync(cancellationToken);
        var response = await agentBridge.SendCommandAsync("GetWorkspaceExists", new { workspaceName, workspaceRoot = root }, cancellationToken);
        if (!response.Success || response.Data == null)
            return false;

        var data = AgentResponseJson.DeserializeAgentResponse<AgentWorkspaceExistsResponse>(response.Data);
        return data?.Exists ?? false;
    }

    public async Task<int> GetRepositoryCountAsync(string workspaceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return 0;

        var root = await GetRootPathAsync(cancellationToken);
        var response = await agentBridge.SendCommandAsync("GetWorkspaceRepositories", new { workspaceName, workspaceRoot = root }, cancellationToken);
        if (!response.Success || response.Data == null)
            return 0;

        var data = AgentResponseJson.DeserializeAgentResponse<AgentRepositoriesListResponse>(response.Data);
        return data?.Repositories?.Count ?? 0;
    }

    public async Task<IReadOnlyList<(string Name, string? OriginUrl)>> GetWorkspaceRepositoryInfosAsync(
        string workspaceName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return Array.Empty<(string, string?)>();

        var root = await GetRootPathAsync(cancellationToken);
        var response = await agentBridge.SendCommandAsync("GetWorkspaceRepositories", new { workspaceName, workspaceRoot = root }, cancellationToken);
        if (!response.Success || response.Data == null)
            return Array.Empty<(string, string?)>();

        var data = AgentResponseJson.DeserializeAgentResponse<AgentWorkspaceRepositoriesResponse>(response.Data);
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

    public async Task CreateDirectoryAsync(string workspaceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return;

        var root = await GetRootPathAsync(cancellationToken);
        await agentBridge.SendCommandAsync("EnsureWorkspace", new { workspaceName, workspaceRoot = root }, cancellationToken);
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


    private static string SanitizeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "workspace";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Trim().Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "workspace" : sanitized;
    }
}
