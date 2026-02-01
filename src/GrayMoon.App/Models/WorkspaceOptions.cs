namespace GrayMoon.App.Models;

public class WorkspaceOptions
{
    public string RootPath { get; set; } = @"C:\Projectes";

    public int MaxConcurrentGitOperations { get; set; } = 8;

    /// <summary>Base URL of the app (e.g. https://graymoon.example.com). If set, post-commit hooks will be created to POST WorkflowId and RepoId to /api/sync after each commit. Leave empty to use PostCommitHookPort with 127.0.0.1.</summary>
    public string? PostCommitHookBaseUrl { get; set; }

    /// <summary>Port only for post-commit hook URL. When set (and PostCommitHookBaseUrl is empty), hooks use http://127.0.0.1:{port}. Typical value: 8384.</summary>
    public int? PostCommitHookPort { get; set; }
}
