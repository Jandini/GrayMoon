using System.Text.Json;

namespace GrayMoon.App.Services;

public class WorkspaceService(IAgentBridge agentBridge, ILogger<WorkspaceService> logger)
{
    private string? _cachedRootPath;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public string? RootPath => _cachedRootPath;

    public async Task<string?> GetRootPathAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedRootPath != null)
            return _cachedRootPath;

        if (!agentBridge.IsAgentConnected)
            return null;

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedRootPath != null)
                return _cachedRootPath;

            if (!agentBridge.IsAgentConnected)
                return null;

            var response = await agentBridge.SendCommandAsync("GetWorkspaceRoot", new { }, cancellationToken);
            if (response.Success && response.Data != null)
            {
                var root = GetProperty<string>(response.Data, "workspaceRoot");
                if (!string.IsNullOrWhiteSpace(root))
                {
                    _cachedRootPath = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    logger.LogInformation("Fetched workspace root from agent: {RootPath}", _cachedRootPath);
                    return _cachedRootPath;
                }
            }

            logger.LogWarning("Failed to fetch workspace root from agent");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error fetching workspace root from agent");
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

        var response = await agentBridge.SendCommandAsync("GetWorkspaceExists", new { workspaceName }, cancellationToken);
        if (!response.Success || response.Data == null)
            return false;

        return GetProperty<bool>(response.Data, "exists");
    }

    public async Task<int> GetRepositoryCountAsync(string workspaceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return 0;

        var response = await agentBridge.SendCommandAsync("GetWorkspaceRepositories", new { workspaceName }, cancellationToken);
        if (!response.Success || response.Data == null)
            return 0;

        var repos = GetProperty<string[]>(response.Data, "repositories");
        return repos?.Length ?? 0;
    }

    public async Task<IReadOnlyList<(string Name, string? OriginUrl)>> GetWorkspaceRepositoryInfosAsync(
        string workspaceName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return Array.Empty<(string, string?)>();

        var response = await agentBridge.SendCommandAsync("GetWorkspaceRepositories", new { workspaceName }, cancellationToken);
        if (!response.Success || response.Data == null)
            return Array.Empty<(string, string?)>();

        var json = response.Data is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(response.Data);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("repositoryInfos", out var infosEl) || infosEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<(string, string?)>();

        var list = new List<(string Name, string? OriginUrl)>();
        foreach (var el in infosEl.EnumerateArray())
        {
            var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var origin = el.TryGetProperty("originUrl", out var o) ? o.GetString() : null;
            list.Add((name, origin));
        }

        return list;
    }

    public async Task CreateDirectoryAsync(string workspaceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return;

        await agentBridge.SendCommandAsync("EnsureWorkspace", new { workspaceName }, cancellationToken);
        logger.LogInformation("Created workspace directory: {Name}", workspaceName);
    }

    /// <summary>Refreshes the cached workspace root from the agent. Call this when agent connects.</summary>
    public async Task RefreshRootPathAsync(CancellationToken cancellationToken = default)
    {
        if (!agentBridge.IsAgentConnected)
        {
            _cachedRootPath = null;
            return;
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            var response = await agentBridge.SendCommandAsync("GetWorkspaceRoot", new { }, cancellationToken);
            if (response.Success && response.Data != null)
            {
                var root = GetProperty<string>(response.Data, "workspaceRoot");
                if (!string.IsNullOrWhiteSpace(root))
                {
                    _cachedRootPath = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    logger.LogInformation("Refreshed workspace root from agent: {RootPath}", _cachedRootPath);
                }
                else
                {
                    _cachedRootPath = null;
                }
            }
            else
            {
                _cachedRootPath = null;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error refreshing workspace root from agent");
            _cachedRootPath = null;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>Clears the cached workspace root. Call this when agent disconnects.</summary>
    public void ClearCachedRootPath()
    {
        _cachedRootPath = null;
    }

    private static T? GetProperty<T>(object data, string name)
    {
        if (data == null) return default;
        var json = data is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(data);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(name, out var prop))
            return default;
        if (typeof(T) == typeof(bool))
            return (T)(object)prop.GetBoolean();
        if (typeof(T) == typeof(string[]))
            return (T)(object)prop.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        if (typeof(T) == typeof(int))
            return (T)(object)prop.GetInt32();
        return (T)Convert.ChangeType(prop.GetString() ?? "", typeof(T))!;
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
