using System.Text.Json;
using GrayMoon.App.Models;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services;

public class WorkspaceService(IOptions<WorkspaceOptions> options, IAgentBridge agentBridge, ILogger<WorkspaceService> logger)
{
    private readonly string _rootPath = GetRootPath(options?.Value?.RootPath);
    private static string GetRootPath(string? rootPath) =>
        string.IsNullOrWhiteSpace(rootPath)
            ? @"C:\Workspace"
            : rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public string RootPath => _rootPath;

    public string GetWorkspacePath(string workspaceName)
    {
        var safeName = SanitizeDirectoryName(workspaceName);
        return Path.Combine(_rootPath, safeName);
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
