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
}
