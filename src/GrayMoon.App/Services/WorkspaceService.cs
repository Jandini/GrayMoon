using GrayMoon.App.Models;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services;

public class WorkspaceService
{
    private readonly string _rootPath;
    private readonly ILogger<WorkspaceService> _logger;

    public WorkspaceService(IOptions<WorkspaceOptions> options, ILogger<WorkspaceService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var rootPath = options?.Value?.RootPath;
        _rootPath = string.IsNullOrWhiteSpace(rootPath)
            ? @"C:\Projectes"
            : rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public string RootPath => _rootPath;

    public string GetWorkspacePath(string workspaceName)
    {
        var safeName = SanitizeDirectoryName(workspaceName);
        return Path.Combine(_rootPath, safeName);
    }

    public bool DirectoryExists(string workspaceName)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
        {
            return false;
        }

        var path = GetWorkspacePath(workspaceName);
        return Directory.Exists(path);
    }

    public int GetRepositoryCount(string workspaceName)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
        {
            return 0;
        }

        var path = GetWorkspacePath(workspaceName);
        if (!Directory.Exists(path))
        {
            return 0;
        }

        return Directory.GetDirectories(path).Length;
    }

    public void CreateDirectory(string workspaceName)
    {
        var path = GetWorkspacePath(workspaceName);

        if (Directory.Exists(path))
        {
            _logger.LogDebug("Workspace directory already exists: {Path}", path);
            return;
        }

        Directory.CreateDirectory(path);
        _logger.LogInformation("Created workspace directory: {Path}", path);
    }

    private static string SanitizeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "workspace";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Trim().Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "workspace" : sanitized;
    }
}
