namespace GrayMoon.App.Models;

public class WorkspaceOptions
{
    public string RootPath { get; set; } = @"C:\Projectes";

    public int MaxConcurrentGitOperations { get; set; } = 8;

    /// <summary>Base URL of the app (e.g. https://graymoon.example.com). If set, post-commit hooks will be created to POST WorkflowId and RepoId to /api/sync after each commit.</summary>
    public string? PostCommitHookBaseUrl { get; set; }
}
