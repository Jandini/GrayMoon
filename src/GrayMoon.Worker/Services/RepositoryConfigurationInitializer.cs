using GrayMoon.Worker.Abstractions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

/// <summary>
/// LibGit2Sharp implementation of <see cref="IRepositoryConfigurationInitializer"/>. Each call that has to touch the
/// repository opens a short-lived <see cref="Repository"/> under a mutating <see cref="IRepositoryAccess"/> lease
/// (so a folder under removal is never written to) and disposes it before returning. Successes are remembered in a
/// small bounded set so the hot paths (every Feature create/remove) cost a lookup; failures are never remembered,
/// so they are retried. The memory is per process: a setting someone unsets externally is reconciled on the next
/// Worker start (or when the bounded set rolls over), not on every call.
/// Lock order: access lease, then the path's stripe lock; the stripe lock is never held while waiting on anything else.
/// </summary>
public sealed class RepositoryConfigurationInitializer(
    IRepositoryAccess? access = null,
    ILogger<RepositoryConfigurationInitializer>? logger = null,
    bool? isWindows = null) : IRepositoryConfigurationInitializer
{
    private const string LongPathsKey = "core.longpaths";
    private const int StripeCount = 32;
    private const int MaxRemembered = 1024;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly bool _windows = isWindows ?? OperatingSystem.IsWindows();
    private readonly object[] _stripes = Enumerable.Range(0, StripeCount).Select(_ => new object()).ToArray();
    private readonly object _memoLock = new();
    private readonly HashSet<string> _configured = new(PathComparer);

    public RepositoryConfigurationResult EnsureWindowsLongPaths(string repositoryPath, CancellationToken ct)
    {
        if (!_windows)
            return RepositoryConfigurationResult.NotApplicable;

        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            logger?.LogWarning("Could not set {Key}: repository not found at {RepoPath}.", LongPathsKey, repositoryPath);
            return RepositoryConfigurationResult.Failed;
        }

        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        if (IsRemembered(key))
            return RepositoryConfigurationResult.AlreadyConfigured;

        // A local config write is a change, so it is a mutating use: refused while a removal covers the folder.
        IRepositoryAccessLease? lease = null;
        if (access is not null)
            lease = access.TryAcquireShared(repositoryPath, RepositoryAccessKind.Mutating) ?? throw new PathUnderRemovalException(repositoryPath);
        using var leaseScope = lease;

        lock (_stripes[(PathComparer.GetHashCode(key) & int.MaxValue) % StripeCount])
        {
            if (IsRemembered(key))
                return RepositoryConfigurationResult.AlreadyConfigured;

            RepositoryConfigurationResult result;
            try
            {
                ct.ThrowIfCancellationRequested();
                lease?.Yield.ThrowIfCancellationRequested();
                result = ReadAndSet(repositoryPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RepositoryNotFoundException ex)
            {
                logger?.LogWarning("Could not set {Key}: {RepoPath} is not an accessible Git repository. {Error}", LongPathsKey, repositoryPath, ex.Message);
                return RepositoryConfigurationResult.Failed;
            }
            catch (LockedFileException ex)
            {
                logger?.LogWarning("Could not set {Key} for {RepoPath}: the Git config file is locked. A later call will retry. {Error}", LongPathsKey, repositoryPath, ex.Message);
                return RepositoryConfigurationResult.Failed;
            }
            catch (LibGit2SharpException ex)
            {
                logger?.LogWarning(ex, "Could not set {Key} for {RepoPath}; very long paths in Feature worktrees may fail.", LongPathsKey, repositoryPath);
                return RepositoryConfigurationResult.Failed;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                logger?.LogWarning(ex, "Could not set {Key} for {RepoPath}: the Git config is not writable.", LongPathsKey, repositoryPath);
                return RepositoryConfigurationResult.Failed;
            }

            Remember(key);
            ct.ThrowIfCancellationRequested();
            return result;
        }
    }

    // Opens the repository, reads only the repository-local level (never the layered effective value, so an inherited
    // global "true" does not stop GrayMoon from recording its own policy), writes when it is not true, and closes.
    // Config.Set(..., Local) targets the common repository config, which every linked worktree shares.
    private RepositoryConfigurationResult ReadAndSet(string repositoryPath)
    {
        // libgit2 gives a linked worktree its own (empty) repository-level config, so the shared config is reached by
        // opening the common git directory itself. Never touches config.worktree, global or system.
        using var repository = new Repository(ResolveCommonGitDir(repositoryPath) ?? repositoryPath);
        var local = repository.Config.Get<bool>(LongPathsKey, ConfigurationLevel.Local);
        if (local?.Value == true)
            return RepositoryConfigurationResult.AlreadyConfigured;

        repository.Config.Set(LongPathsKey, true, ConfigurationLevel.Local);
        logger?.LogDebug("Set {Key}=true in the local config of {RepoPath}.", LongPathsKey, repositoryPath);
        return RepositoryConfigurationResult.Configured;
    }

    // For a linked worktree: the main repository's git directory (found through the worktree's "commondir" file).
    // Null for a main repository, which is opened as given.
    private static string? ResolveCommonGitDir(string repositoryPath)
    {
        string gitDir;
        using (var probe = new Repository(repositoryPath))
            gitDir = probe.Info.Path;

        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonDirFile))
            return null;

        var relative = File.ReadAllText(commonDirFile).Trim();
        return relative.Length == 0 ? null : Path.GetFullPath(Path.Combine(gitDir, relative));
    }

    private bool IsRemembered(string key)
    {
        lock (_memoLock)
            return _configured.Contains(key);
    }

    private void Remember(string key)
    {
        lock (_memoLock)
        {
            if (_configured.Count >= MaxRemembered)
                _configured.Clear(); // bounded: paths get reused and configs change, so forgetting is always safe
            _configured.Add(key);
        }
    }
}
