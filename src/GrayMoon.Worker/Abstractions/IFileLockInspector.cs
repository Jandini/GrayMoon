using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Finds the local processes that keep a file or folder in use, so Remove Feature can name what to close (or, after the user
/// confirms, end) instead of a bare Git or IO error. Read-only: never closes, kills or signals a process. Called before Remove
/// (the Remove Feature dialog check), on "Refresh blockers", around a user-confirmed kill, and on the removal failure path;
/// never before every delete or in a loop.
/// </summary>
public interface IFileLockInspector
{
    /// <summary>
    /// Looks for processes that have a file or folder under <paramref name="path"/> open, run a program from it, or whose
    /// current directory is inside it. Never throws for an inspection problem (missing path, access denied, time limit,
    /// unsupported platform); those come back as an empty or partial result with
    /// <see cref="FileLockInspectionResult.MayBeIncomplete"/> set.
    /// </summary>
    Task<FileLockInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inspects several paths, returning one result per path in the same order. A process is attributed to the most specific
    /// path that contains what it holds, so nested folders are not reported twice. The default runs <see cref="InspectAsync"/>
    /// per path; implementations that can check all paths in one pass override it.
    /// </summary>
    async Task<IReadOnlyList<FileLockInspectionResult>> InspectManyAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        var results = new List<FileLockInspectionResult>(paths.Count);
        foreach (var path in paths)
            results.Add(await InspectAsync(path, cancellationToken));
        return results;
    }
}
