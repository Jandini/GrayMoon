using GrayMoon.Worker.Abstractions;
using LibGit2Sharp;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Reads one raw, effective (layered: local, global, XDG, system, includes) string value from a repository's Git
/// configuration in-process, in place of <c>git config --get &lt;key&gt;</c>. It only answers when it can match what
/// native Git would print; otherwise it says so and the caller keeps its native command. Cases deliberately left to
/// native Git: configuration redirected by the environment (<c>GIT_CONFIG_*</c>, which libgit2 does not honour),
/// linked worktrees, per-worktree configuration (<c>config.worktree</c>), conditional includes (<c>includeIf</c>),
/// and any failure to open the repository (for example dubious ownership). The repository is opened under a read-only <see cref="IRepositoryAccess"/> lease and always disposed.
/// </summary>
internal static class LibGit2SharpConfigReader
{
    private static readonly string[] RedirectingEnvironment =
    [
        "GIT_CONFIG", "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_NOSYSTEM",
    ];

    /// <summary>
    /// <c>Handled</c> is false when the caller must use native Git. When true, <c>Value</c> is the last value set for
    /// <paramref name="key"/> (what <c>git config --get</c> prints), or null when the key is not set anywhere.
    /// Throws <see cref="PathUnderRemovalException"/> while a removal covers the folder.
    /// </summary>
    public static (bool Handled, string? Value) TryGetString(string repositoryPath, string key, IRepositoryAccess? access, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (RedirectingEnvironment.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
            return (false, null);

        IRepositoryAccessLease? lease = null;
        if (access is not null)
            lease = access.TryAcquireShared(repositoryPath, RepositoryAccessKind.ReadOnly) ?? throw new PathUnderRemovalException(repositoryPath);

        using var leaseScope = lease;
        try
        {
            using var repository = new Repository(repositoryPath);
            // Not shown to match native git, so native keeps them: a linked worktree (libgit2 does not layer the shared
            // common-dir config under it), per-worktree config, and conditional includes (libgit2 does not evaluate
            // includeIf gitdir: the way git does, so a value reached through one could silently go missing).
            var gitDir = repository.Info.Path;
            if (File.Exists(Path.Combine(gitDir, "commondir")) || File.Exists(Path.Combine(gitDir, "config.worktree")))
                return (false, null);
            if (repository.Config.Any(entry => entry.Key.StartsWith("includeif.", StringComparison.OrdinalIgnoreCase)))
                return (false, null);

            lease?.Yield.ThrowIfCancellationRequested();
            var value = repository.Config.Get<string>(key)?.Value;
            ct.ThrowIfCancellationRequested();
            return (true, value);
        }
        catch (LibGit2SharpException)
        {
            return (false, null);
        }
    }
}
