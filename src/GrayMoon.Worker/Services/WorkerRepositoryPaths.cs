namespace GrayMoon.Worker.Services;

public static class WorkerRepositoryPaths
{
    /// <summary>
    /// Absolute working-tree path of <paramref name="repositoryName"/> inside <paramref name="workspacePath"/>.
    /// Every command resolves its repository here, so this is where the name is checked: the App is not a trusted
    /// source of names, and a rooted or ".." name would resolve outside the workspace folder.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not a single folder name.</exception>
    public static string Resolve(string workspacePath, string repositoryName, string? workspaceRepositoryName)
    {
        if (GetRepositoryNameRejection(repositoryName) is { } rejection)
            throw new ArgumentException(rejection, nameof(repositoryName));
        return IsWorkspaceRepository(repositoryName, workspaceRepositoryName)
            ? workspacePath
            : Path.Combine(workspacePath, repositoryName);
    }

    // The Windows set on every OS, so a name is judged the same wherever the Worker runs.
    private static readonly char[] InvalidRepositoryNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Null when <paramref name="repositoryName"/> is one safe folder name; otherwise why it is refused. Refused:
    /// empty, "." and "..", a separator or drive colon, a rooted path, control and wildcard characters, and a
    /// trailing dot or space (Windows drops those, so the folder used would not be the one named).
    /// </summary>
    public static string? GetRepositoryNameRejection(string? repositoryName)
    {
        if (string.IsNullOrWhiteSpace(repositoryName))
            return "Repository name is empty.";
        if (repositoryName is "." or ".."
            || repositoryName.IndexOfAny(InvalidRepositoryNameChars) >= 0
            || repositoryName.Any(char.IsControl)
            || Path.IsPathRooted(repositoryName)
            || repositoryName[0] == ' '
            || repositoryName[^1] is '.' or ' ')
        {
            return "Repository name is not a valid folder name.";
        }

        return null;
    }

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
        // Every command resolves its workspace here, so this is where the Worker learns which roots it manages.
        KnownWorkspaceRoots.Shared.Add(root);
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
