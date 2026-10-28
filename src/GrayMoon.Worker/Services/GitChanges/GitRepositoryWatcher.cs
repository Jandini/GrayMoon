using GrayMoon.Worker.Abstractions;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services.GitChanges;

/// <summary>
/// Watches one repository's working tree and relevant <c>.git</c> metadata paths, raising
/// <see cref="Changed"/> as an invalidation hint only - the caller must always re-run an authoritative
/// git status scan, never reconstruct state from the watcher event itself. Working-tree events also raise
/// <see cref="Observed"/> with a UTC receipt timestamp for coverage metadata; <c>.git</c> metadata only
/// invalidates and is not recorded as a normal file observation.
/// </summary>
public sealed class GitRepositoryWatcher : IDisposable, IReleasable, IRepositoryAccessObserver
{
    private static readonly string[] RelevantGitMetadataNames =
    [
        "index", "HEAD", "packed-refs", "MERGE_HEAD", "CHERRY_PICK_HEAD", "rebase-merge", "rebase-apply",
    ];

    private readonly string _repoPath;
    private readonly ILogger _logger;
    private FileSystemWatcher? _workTreeWatcher;
    private FileSystemWatcher? _gitDirWatcher;
    private bool _disposed;

    // Removals in progress strictly beneath this repository (it is an ancestor of a folder being deleted). While any is
    // open, events are ignored instead of reported or restarting the watcher mid-delete; one full refresh follows.
    private int _pausedBy;
    private volatile bool _missedWhilePaused;

    // Immediate child directories that are themselves Git working trees (nested repository or submodule).
    // A nested .git is a physical boundary, so events below such a directory never belong to this repository.
    // The set is treated as immutable and replaced wholesale on refresh, so event threads never see a half-built set.
    private volatile HashSet<string> _nestedRepoRoots = new(StringComparer.OrdinalIgnoreCase);

    public GitRepositoryWatcher(string repoPath, ILogger logger)
    {
        _repoPath = repoPath;
        _logger = logger;
        Start();
    }

    /// <summary>Invalidation hint - a relevant path changed. Never carries enough information to update
    /// state directly; the source of truth is always a fresh git status scan.</summary>
    public event Action? Changed;

    /// <summary>Working-tree filesystem observation (UTC receipt time + path/kind). Supplementary metadata
    /// only - does not replace <see cref="Changed"/> and must not drive Git state.</summary>
    public event Action<GitRepositoryObservedChange>? Observed;

    /// <summary>The watcher overflowed or failed and was recreated; callers should treat the current
    /// snapshot as potentially stale and trigger a full refresh.</summary>
    public event Action? Overflowed;

    /// <summary>True while the work-tree OS watcher exists and is raising events. Diagnostic only.</summary>
    public bool IsWorkTreeWatching => _workTreeWatcher is { EnableRaisingEvents: true };

    /// <summary>True while the <c>.git</c> metadata OS watcher exists and is raising events. Diagnostic only.</summary>
    public bool IsMetadataWatching => _gitDirWatcher is { EnableRaisingEvents: true };

    /// <summary>Why no metadata watcher is running (null when it is). A linked worktree has a <c>.git</c> file, not a directory.</summary>
    public string? MetadataWatcherSkipReason { get; private set; }

    /// <summary>
    /// True when every OS watcher this repository needs is running: the work tree and, when the repository has a
    /// readable git metadata directory, that directory too. This is what an entry's health means; an entry whose
    /// watcher failed to start or restart is degraded and must not be reported as watched.
    /// </summary>
    public bool IsHealthy => !_disposed && IsWorkTreeWatching && (_metadataDirectoryResolved ? IsMetadataWatching : true);

    // Larger than the 8 KB default so a burst (checkout, build output) is less likely to overflow the buffer.
    private const int WatcherBufferBytes = 64 * 1024;

    private bool _metadataDirectoryResolved;

    /// <summary>
    /// Finds the directory holding this repository's index, HEAD and ref markers. A normal checkout has a
    /// <c>.git</c> directory; a linked worktree has a <c>.git</c> file whose <c>gitdir:</c> line names a
    /// per-worktree directory inside the main repository's <c>.git/worktrees</c>. Internal for unit tests.
    /// </summary>
    internal static string? ResolveGitMetadataDirectory(string repoPath, out string? skipReason)
    {
        skipReason = null;
        var dotGit = Path.Combine(repoPath, ".git");
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        if (!File.Exists(dotGit))
        {
            skipReason = ".git not found";
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines(dotGit))
            {
                const string prefix = "gitdir:";
                if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = line[prefix.Length..].Trim();
                var resolved = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(repoPath, target));
                if (Directory.Exists(resolved))
                {
                    return resolved;
                }

                skipReason = $".git file points to a missing directory ({resolved})";
                return null;
            }

            skipReason = ".git file has no gitdir line";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            skipReason = $".git file is unreadable ({ex.GetType().Name})";
        }

        return null;
    }

    private void Start()
    {
        try
        {
            RefreshNestedRepoRoots();
            _workTreeWatcher = new FileSystemWatcher(_repoPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                InternalBufferSize = WatcherBufferBytes,
            };
            _workTreeWatcher.Changed += OnWorkTreeChanged;
            _workTreeWatcher.Created += OnWorkTreeCreated;
            _workTreeWatcher.Deleted += OnWorkTreeDeleted;
            _workTreeWatcher.Renamed += OnWorkTreeRenamed;
            _workTreeWatcher.Error += OnError;
            _workTreeWatcher.EnableRaisingEvents = true;

            var gitDir = ResolveGitMetadataDirectory(_repoPath, out var skipReason);
            MetadataWatcherSkipReason = skipReason;
            _metadataDirectoryResolved = gitDir is not null;
            if (gitDir is not null)
            {
                _gitDirWatcher = new FileSystemWatcher(gitDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = WatcherBufferBytes,
                };
                _gitDirWatcher.Changed += OnGitMetadataEvent;
                _gitDirWatcher.Created += OnGitMetadataEvent;
                _gitDirWatcher.Deleted += OnGitMetadataEvent;
                _gitDirWatcher.Renamed += OnGitMetadataEvent;
                _gitDirWatcher.Error += OnError;
                _gitDirWatcher.EnableRaisingEvents = true;
            }

            _logger.LogInformation(
                "[GCDiag] git watcher started for {RepoPath}: workTree={WorkTree} metadata={Metadata} metadataSkipReason={SkipReason}",
                _repoPath, IsWorkTreeWatching, IsMetadataWatching, MetadataWatcherSkipReason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GCDiag] Failed to start git repository watcher for {RepoPath}; the entry is degraded until a renewal recovers it", _repoPath);
            // Leave nothing half-started: health is derived from the OS watchers, and a partial set would look watched.
            DisposeWatchers();
        }
    }

    /// <summary>
    /// Restarts the OS watchers when they are not healthy (a failed start or a failed restart after an error).
    /// Returns whether the watcher is healthy afterwards. A restart can miss changes, so a successful recovery
    /// raises <see cref="Overflowed"/> and the caller treats the current snapshot as stale.
    /// </summary>
    public bool TryRecover()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return false;
            }

            if (IsHealthy)
            {
                return true;
            }

            DisposeWatchers();
            Start();
        }

        var healthy = IsHealthy;
        _logger.Log(
            healthy ? LogLevel.Information : LogLevel.Warning,
            "[GCDiag] git watcher recovery for {RepoPath}: healthy={Healthy}",
            _repoPath, healthy);
        if (healthy)
        {
            Overflowed?.Invoke();
        }

        return healthy;
    }

    private readonly object _lifecycleLock = new();

    private void OnWorkTreeChanged(object sender, FileSystemEventArgs e) =>
        HandleWorkTreeEvent(e, GitRepositoryObservedChangeKind.Changed, oldPath: null);

    private void OnWorkTreeCreated(object sender, FileSystemEventArgs e) =>
        HandleWorkTreeEvent(e, GitRepositoryObservedChangeKind.Created, oldPath: null);

    private void OnWorkTreeDeleted(object sender, FileSystemEventArgs e) =>
        HandleWorkTreeEvent(e, GitRepositoryObservedChangeKind.Deleted, oldPath: null);

    private void OnWorkTreeRenamed(object sender, RenamedEventArgs e) =>
        HandleWorkTreeEvent(e, GitRepositoryObservedChangeKind.Renamed, oldPath: e.OldFullPath);

    /// <summary>
    /// Classifies a work-tree FS event into an observation (or null when ignored). Internal for unit tests
    /// without depending on live FileSystemWatcher event counts.
    /// </summary>
    internal static GitRepositoryObservedChange? TryCreateWorkTreeObservation(
        string fullPath,
        GitRepositoryObservedChangeKind kind,
        DateTimeOffset observedAt,
        string? oldPath,
        IReadOnlyCollection<string>? nestedRepoRoots = null)
    {
        if (IsUnderGitDirectory(fullPath))
        {
            return null;
        }

        if (IsUnderNestedRepository(fullPath, nestedRepoRoots))
        {
            return null;
        }

        return new GitRepositoryObservedChange
        {
            ObservedAt = observedAt,
            Path = fullPath,
            Kind = kind,
            OldPath = oldPath,
        };
    }

    private void HandleWorkTreeEvent(FileSystemEventArgs e, GitRepositoryObservedChangeKind kind, string? oldPath)
    {
        if (Volatile.Read(ref _pausedBy) > 0)
        {
            _missedWhilePaused = true;
            return;
        }

        if (kind != GitRepositoryObservedChangeKind.Changed && AffectsNestedRepoSet(e.FullPath))
        {
            RefreshNestedRepoRoots();
        }

        var observation = TryCreateWorkTreeObservation(e.FullPath, kind, DateTimeOffset.UtcNow, oldPath, _nestedRepoRoots);
        if (observation is null)
        {
            return;
        }

        Changed?.Invoke();
        Observed?.Invoke(observation);
    }

    /// <summary>The current set of nested repository roots (full paths). Internal for unit tests.</summary>
    internal IReadOnlyCollection<string> NestedRepoRoots => _nestedRepoRoots;

    /// <summary>Recomputes the nested repository roots from the immediate child directories of the watched repository.</summary>
    internal void RefreshNestedRepoRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var child in Directory.EnumerateDirectories(_repoPath))
            {
                if (WorkerRepositoryPaths.HasGitMetadata(child))
                {
                    roots.Add(Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not enumerate nested repositories under {RepoPath}", _repoPath);
        }

        _nestedRepoRoots = roots;
    }

    // True for an immediate child directory of the watched repository, or for the .git entry directly inside one
    // (git init / git clone create the folder first and its .git moments later).
    private bool AffectsNestedRepoSet(string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
        {
            return false;
        }

        if (IsSamePath(parent, _repoPath))
        {
            return true;
        }

        return string.Equals(Path.GetFileName(fullPath), ".git", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(parent) is { } grandParent
            && IsSamePath(grandParent, _repoPath);
    }

    private static bool IsSamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderNestedRepository(string fullPath, IReadOnlyCollection<string>? nestedRepoRoots)
    {
        if (nestedRepoRoots is null || nestedRepoRoots.Count == 0)
        {
            return false;
        }

        var separator = Path.DirectorySeparatorChar;
        foreach (var root in nestedRepoRoots)
        {
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(root + separator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderGitDirectory(string fullPath)
    {
        var separator = Path.DirectorySeparatorChar;
        return fullPath.Contains($"{separator}.git{separator}", StringComparison.OrdinalIgnoreCase)
            || fullPath.EndsWith($"{separator}.git", StringComparison.OrdinalIgnoreCase);
    }

    private void OnGitMetadataEvent(object sender, FileSystemEventArgs e)
    {
        if (Volatile.Read(ref _pausedBy) > 0)
        {
            _missedWhilePaused = true;
            return;
        }

        var name = Path.GetFileName(e.FullPath);
        var parentDirectoryName = Path.GetFileName(Path.GetDirectoryName(e.FullPath) ?? string.Empty);

        var isRelevant =
            RelevantGitMetadataNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            RelevantGitMetadataNames.Contains(parentDirectoryName, StringComparer.OrdinalIgnoreCase) ||
            string.Equals(parentDirectoryName, "refs", StringComparison.OrdinalIgnoreCase) ||
            e.FullPath.Contains($"{Path.DirectorySeparatorChar}refs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

        if (isRelevant)
        {
            // Metadata noise still invalidates status; it is not a working-tree file observation.
            Changed?.Invoke();
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (Volatile.Read(ref _pausedBy) > 0)
        {
            // A delete beneath this folder overflows the buffer by design. Keep the watcher; resume reports it.
            _missedWhilePaused = true;
            return;
        }

        _logger.LogWarning(e.GetException(), "[GCDiag] Git repository watcher overflow/failure for {RepoPath}; recreating watcher", _repoPath);
        lock (_lifecycleLock)
        {
            DisposeWatchers();
        }

        // The discontinuity is recorded (and a refresh queued) before the restart, so it is never lost if the restart fails.
        Overflowed?.Invoke();

        if (!_disposed)
        {
            lock (_lifecycleLock)
            {
                if (!_disposed)
                {
                    Start();
                }
            }

            _logger.Log(
                IsWorkTreeWatching ? LogLevel.Information : LogLevel.Warning,
                "[GCDiag] git watcher restart after error finished for {RepoPath}: workTreeWatching={WorkTree}",
                _repoPath, IsWorkTreeWatching);
        }
    }

    /// <inheritdoc />
    public void Release() => Dispose();

    /// <inheritdoc />
    public void ClaimBeneathStarted() => Interlocked.Increment(ref _pausedBy);

    /// <inheritdoc />
    public void ClaimBeneathEnded()
    {
        if (Interlocked.Decrement(ref _pausedBy) > 0 || _disposed)
        {
            return;
        }

        if (_missedWhilePaused)
        {
            _missedWhilePaused = false;
            Overflowed?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lifecycleLock)
        {
            _disposed = true;
            DisposeWatchers();
        }
    }

    private void DisposeWatchers()
    {
        if (_workTreeWatcher != null)
        {
            _workTreeWatcher.EnableRaisingEvents = false;
            _workTreeWatcher.Changed -= OnWorkTreeChanged;
            _workTreeWatcher.Created -= OnWorkTreeCreated;
            _workTreeWatcher.Deleted -= OnWorkTreeDeleted;
            _workTreeWatcher.Renamed -= OnWorkTreeRenamed;
            _workTreeWatcher.Error -= OnError;
            _workTreeWatcher.Dispose();
            _workTreeWatcher = null;
        }

        if (_gitDirWatcher != null)
        {
            _gitDirWatcher.EnableRaisingEvents = false;
            _gitDirWatcher.Changed -= OnGitMetadataEvent;
            _gitDirWatcher.Created -= OnGitMetadataEvent;
            _gitDirWatcher.Deleted -= OnGitMetadataEvent;
            _gitDirWatcher.Renamed -= OnGitMetadataEvent;
            _gitDirWatcher.Error -= OnError;
            _gitDirWatcher.Dispose();
            _gitDirWatcher = null;
        }
    }
}
