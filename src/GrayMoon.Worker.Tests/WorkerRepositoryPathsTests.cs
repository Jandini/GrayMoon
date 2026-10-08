using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public sealed class WorkerRepositoryPathsTests
{
    private const string WorkspacePath = @"C:\workspaces\ws";

    [Fact]
    public void Resolve_returns_subfolder_when_no_workspace_repository()
    {
        Assert.Equal(Path.Combine(WorkspacePath, "Api"), WorkerRepositoryPaths.Resolve(WorkspacePath, "Api", null));
        Assert.Equal(Path.Combine(WorkspacePath, "Api"), WorkerRepositoryPaths.Resolve(WorkspacePath, "Api", "  "));
    }

    [Fact]
    public void Resolve_returns_workspace_path_for_workspace_repository_case_insensitive()
    {
        Assert.Equal(WorkspacePath, WorkerRepositoryPaths.Resolve(WorkspacePath, "Workspace-Repo", "workspace-repo"));
        Assert.True(WorkerRepositoryPaths.IsWorkspaceRepository("WORKSPACE-REPO", "workspace-repo"));
    }

    [Fact]
    public void Resolve_returns_subfolder_for_other_repository_when_workspace_repository_set()
    {
        Assert.Equal(Path.Combine(WorkspacePath, "Api"), WorkerRepositoryPaths.Resolve(WorkspacePath, "Api", "Workspace-Repo"));
        Assert.False(WorkerRepositoryPaths.IsWorkspaceRepository("Api", "Workspace-Repo"));
    }

    [Fact]
    public void HasGitMetadata_true_for_dir_and_file()
    {
        var root = Directory.CreateTempSubdirectory("graymoon-paths-").FullName;
        try
        {
            var withDir = Path.Combine(root, "dir");
            Directory.CreateDirectory(Path.Combine(withDir, ".git"));
            var withFile = Path.Combine(root, "file");
            Directory.CreateDirectory(withFile);
            File.WriteAllText(Path.Combine(withFile, ".git"), "gitdir: ../elsewhere");
            var none = Path.Combine(root, "none");
            Directory.CreateDirectory(none);

            Assert.True(WorkerRepositoryPaths.HasGitMetadata(withDir));
            Assert.True(WorkerRepositoryPaths.HasGitMetadata(withFile));
            Assert.False(WorkerRepositoryPaths.HasGitMetadata(none));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best-effort */ }
        }
    }
}
