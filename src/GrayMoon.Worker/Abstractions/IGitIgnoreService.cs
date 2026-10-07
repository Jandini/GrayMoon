namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Single source of truth for Git ignore decisions. Opens the repository once per logical operation;
/// all rule evaluation is delegated to libgit2. Never shells out to <c>git check-ignore</c>.
/// </summary>
public interface IGitIgnoreService
{
    /// <summary>
    /// Opens <paramref name="repositoryPath"/> (a work-tree root, including a linked worktree).
    /// Throws <see cref="GitIgnoreException"/> when it cannot be opened (not a repository, bare,
    /// ownership/safe.directory refusal, corrupt). There is no fallback to the Git CLI.
    /// </summary>
    IGitIgnoreSession Open(string repositoryPath);
}

/// <summary>
/// A short-lived, single-threaded view of one repository's ignore state. Dispose it as soon as the
/// logical operation (one discovery scan, one stage classification) completes.
/// </summary>
public interface IGitIgnoreSession : IDisposable
{
    /// <summary>
    /// True when the path is untracked and matches an ignore rule (Git's definition: tracked paths are
    /// never excluded). For <see cref="GitPathKind.Directory"/>, true only when the directory also
    /// contains no tracked entries. <paramref name="relativePath"/> must already be validated:
    /// repository-relative and '/'-separated; anything else throws <see cref="ArgumentException"/>.
    /// </summary>
    bool IsExcluded(string relativePath, GitPathKind kind);

    /// <summary>
    /// Splits validated repository-relative paths into those that may be staged and excluded untracked
    /// ones. File versus directory is decided from the work tree (a missing path is treated as a file).
    /// Input order is preserved in both lists.
    /// </summary>
    GitStageSelection SelectStageable(IReadOnlyList<string> relativePaths);
}

public enum GitPathKind
{
    File,
    Directory,
}

public sealed record GitStageSelection(IReadOnlyList<string> Stageable, IReadOnlyList<string> ExcludedUntracked);

/// <summary>Ignore state could not be determined. Callers must fail the operation, never proceed as if nothing is ignored.</summary>
public sealed class GitIgnoreException : Exception
{
    public GitIgnoreException(string repositoryPath, string message, Exception? innerException = null)
        : base($"{message} (repository: {repositoryPath})", innerException)
    {
        RepositoryPath = repositoryPath;
    }

    public string RepositoryPath { get; }
}
