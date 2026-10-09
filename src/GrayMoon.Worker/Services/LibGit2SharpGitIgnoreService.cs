using GrayMoon.Worker.Abstractions;
using LibGit2Sharp;

namespace GrayMoon.Worker.Services;

/// <summary>LibGit2Sharp-backed <see cref="IGitIgnoreService"/>. Holds no state; each session owns its own <see cref="Repository"/>.</summary>
public sealed class LibGit2SharpGitIgnoreService(IRepositoryAccess? access = null) : IGitIgnoreService
{
    public IGitIgnoreSession Open(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
            throw new ArgumentException("Repository path is required.", nameof(repositoryPath));

        // The open repository holds files in the work tree's git folder for the session's life, so it is a shared use.
        IRepositoryAccessLease? lease = null;
        if (access is not null)
            lease = access.TryAcquireShared(repositoryPath, RepositoryAccessKind.ReadOnly) ?? throw new PathUnderRemovalException(repositoryPath);

        Repository repository;
        try
        {
            repository = new Repository(repositoryPath);
        }
        catch (LibGit2SharpException ex)
        {
            lease?.Dispose();
            throw new GitIgnoreException(repositoryPath, $"Could not open repository for ignore evaluation: {ex.Message}", ex);
        }

        if (repository.Info.IsBare || string.IsNullOrEmpty(repository.Info.WorkingDirectory))
        {
            repository.Dispose();
            lease?.Dispose();
            throw new GitIgnoreException(repositoryPath, "Repository has no work tree.");
        }

        return new Session(repositoryPath, repository, lease);
    }

    private sealed class Session(string repositoryPath, Repository repository, IRepositoryAccessLease? lease) : IGitIgnoreSession
    {
        private HashSet<string>? _trackedFiles;
        private HashSet<string>? _trackedDirectories;
        private bool _disposed;

        public bool IsExcluded(string relativePath, GitPathKind kind)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfYielded();
            ValidateRelativePath(relativePath);
            EnsureTrackedSets();

            try
            {
                return kind == GitPathKind.Directory
                    ? !_trackedDirectories!.Contains(relativePath) && repository.Ignore.IsPathIgnored(relativePath + "/")
                    : !_trackedFiles!.Contains(relativePath) && repository.Ignore.IsPathIgnored(relativePath);
            }
            catch (LibGit2SharpException ex)
            {
                throw new GitIgnoreException(repositoryPath, $"Ignore evaluation failed for '{relativePath}': {ex.Message}", ex);
            }
        }

        public GitStageSelection SelectStageable(IReadOnlyList<string> relativePaths)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfYielded();
            var workDir = repository.Info.WorkingDirectory;
            var stageable = new List<string>(relativePaths.Count);
            var excluded = new List<string>();
            foreach (var path in relativePaths)
            {
                ValidateRelativePath(path);
                var kind = Directory.Exists(Path.Combine(workDir, path)) ? GitPathKind.Directory : GitPathKind.File;
                (IsExcluded(path, kind) ? excluded : stageable).Add(path);
            }

            return new GitStageSelection(stageable, excluded);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            repository.Dispose();
            lease?.Dispose();
        }

        // The folder is being removed: stop using the repository so the holder disposes the session and frees the folder.
        private void ThrowIfYielded()
        {
            if (lease is { Yield.IsCancellationRequested: true })
                throw new PathUnderRemovalException(repositoryPath);
        }

        private void EnsureTrackedSets()
        {
            if (_trackedFiles is not null)
                return;

            var comparer = repository.Config.GetValueOrDefault("core.ignorecase", false)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var files = new HashSet<string>(comparer);
            var directories = new HashSet<string>(comparer);
            foreach (var entry in repository.Index)
            {
                var path = entry.Path;
                files.Add(path);
                var slash = path.LastIndexOf('/');
                while (slash > 0)
                {
                    var directory = path[..slash];
                    if (!directories.Add(directory))
                        break; // all ancestors already recorded
                    slash = directory.LastIndexOf('/');
                }
            }

            _trackedFiles = files;
            _trackedDirectories = directories;
        }

        // libgit2 does not validate: "../x" and "." report ignored, "\\" and absolute paths report not ignored.
        private static void ValidateRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path is empty.", nameof(path));
            if (path.Contains('\\') || path.StartsWith('/') || Path.IsPathRooted(path) || (path.Length >= 2 && path[1] == ':'))
                throw new ArgumentException($"Path must be repository-relative and '/'-separated: '{path}'.", nameof(path));

            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Path has an invalid segment: '{path}'.", nameof(path));
            }
        }
    }
}
