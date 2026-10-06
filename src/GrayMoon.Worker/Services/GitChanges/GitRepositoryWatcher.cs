using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services.GitChanges;

/// <summary>
/// Watches one repository's working tree and relevant <c>.git</c> metadata paths, raising
/// <see cref="Changed"/> as an invalidation hint only - the caller must always re-run an authoritative
/// git status scan, never reconstruct state from the watcher event itself. Working-tree events also raise
/// <see cref="Observed"/> with a UTC receipt timestamp for coverage metadata; <c>.git</c> metadata only
/// invalidates and is not recorded as a normal file observation.
/// </summary>
public sealed class GitRepositoryWatcher : IDisposable
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

    private void Start()
    {
        try
        {
            RefreshNestedRepoRoots();
            _workTreeWatcher = new FileSystemWatcher(_repoPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            };
            _workTreeWatcher.Changed += OnWorkTreeChanged;
            _workTreeWatcher.Created += OnWorkTreeCreated;
            _workTreeWatcher.Deleted += OnWorkTreeDeleted;
            _workTreeWatcher.Renamed += OnWorkTreeRenamed;
            _workTreeWatcher.Error += OnError;
            _workTreeWatcher.EnableRaisingEvents = true;

            var gitDir = Path.Combine(_repoPath, ".git");
            if (Directory.Exists(gitDir))
            {
                _gitDirWatcher = new FileSystemWatcher(gitDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                _gitDirWatcher.Changed += OnGitMetadataEvent;
                _gitDirWatcher.Created += OnGitMetadataEvent;
                _gitDirWatcher.Deleted += OnGitMetadataEvent;
                _gitDirWatcher.Renamed += OnGitMetadataEvent;
                _gitDirWatcher.Error += OnError;
                _gitDirWatcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start git repository watcher for {RepoPath}", _repoPath);
        }
    }

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
        _logger.LogWarning(e.GetException(), "Git repository watcher overflow/failure for {RepoPath}; recreating watcher", _repoPath);
        DisposeWatchers();
        Overflowed?.Invoke();

        if (!_disposed)
        {
            Start();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeWatchers();
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
