namespace GrayMoon.App.Models;

public class WorkspaceOptions
{
    public int MaxConcurrentGitOperations { get; set; } = 8;

    /// <summary>Base URL of the app (e.g. https://graymoon.example.com). If set, post-commit hooks will be created to POST WorkflowId and RepoId to /api/sync after each commit. Leave empty to use PostCommitHookPort with 127.0.0.1.</summary>
    public string? PostCommitHookBaseUrl { get; set; }

    /// <summary>Port only for post-commit hook URL. When set (and PostCommitHookBaseUrl is empty), hooks use http://127.0.0.1:{port}. Typical value: 8384.</summary>
    public int? PostCommitHookPort { get; set; }

    /// <summary>Expected time per dependency build in minutes, used to compute push wait timeout. Timeout = (number of dependencies) × this value. Default 1.</summary>
    public double PushWaitDependencyTimeoutMinutesPerDependency { get; set; } = 1.0;
}
