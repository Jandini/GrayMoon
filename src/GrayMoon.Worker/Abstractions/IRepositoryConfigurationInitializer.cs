namespace GrayMoon.Worker.Abstractions;

/// <summary>What <see cref="IRepositoryConfigurationInitializer.EnsureWindowsLongPaths"/> found and did.</summary>
public enum RepositoryConfigurationResult
{
    /// <summary>The policy does not apply on this operating system; nothing was read or written.</summary>
    NotApplicable,

    /// <summary>The repository-local setting was already <c>true</c> (or was confirmed so earlier by this process); nothing was written.</summary>
    AlreadyConfigured,

    /// <summary>The repository-local setting was missing or false and has been set to <c>true</c>.</summary>
    Configured,

    /// <summary>The setting could not be read or written; the reason was logged and a later call tries again.</summary>
    Failed,
}

/// <summary>
/// One-time, in-process repository configuration GrayMoon needs before it works in a managed repository: on Windows,
/// <c>core.longpaths=true</c> in the repository's shared local config, so Feature worktrees with deep trees can be
/// checked out and removed past the 260-character limit. Replaces the per-Feature <c>git config</c> processes.
/// </summary>
public interface IRepositoryConfigurationInitializer
{
    /// <summary>
    /// On Windows, makes sure the repository's own (shared, never global or per-worktree) config has
    /// <c>core.longpaths=true</c>. Cheap after the first success for a path. Never throws for configuration problems
    /// (they return <see cref="RepositoryConfigurationResult.Failed"/>); cancellation, including a folder under
    /// removal (<see cref="PathUnderRemovalException"/>), propagates.
    /// </summary>
    RepositoryConfigurationResult EnsureWindowsLongPaths(string repositoryPath, CancellationToken ct);
}
