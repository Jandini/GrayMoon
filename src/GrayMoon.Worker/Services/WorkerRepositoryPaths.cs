namespace GrayMoon.Worker.Services;

public static class WorkerRepositoryPaths
{
    /// <summary>Absolute working-tree path of <paramref name="repositoryName"/> inside <paramref name="workspacePath"/>.</summary>
    public static string Resolve(string workspacePath, string repositoryName, string? workspaceRepositoryName)
        => IsWorkspaceRepository(repositoryName, workspaceRepositoryName)
            ? workspacePath
            : Path.Combine(workspacePath, repositoryName);

    public static bool IsWorkspaceRepository(string repositoryName, string? workspaceRepositoryName)
        => !string.IsNullOrWhiteSpace(workspaceRepositoryName)
           && string.Equals(repositoryName, workspaceRepositoryName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> contains a .git directory or file (worktree or submodule pointer).</summary>
    public static bool HasGitMetadata(string path)
    {
        var git = Path.Combine(path, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    /// <summary>Absolute path of the workspace folder <paramref name="workspaceName"/> under <paramref name="root"/>.</summary>
    public static string GetWorkspacePath(string root, string workspaceName)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Workspace root path is required.", nameof(root));
        var safe = SanitizeDirectoryName(workspaceName ?? "");
        return Path.Combine(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), safe);
    }

    /// <summary>Names (not paths) of the immediate sub-folders of <paramref name="path"/>; empty when it does not exist.</summary>
    public static string[] GetDirectoryNames(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return [];
        return Directory.GetDirectories(path).Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToArray();
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
