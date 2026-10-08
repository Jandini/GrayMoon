using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Finds the local processes that keep a file or folder in use, so a failed delete (for example Remove Feature) can say what to
/// close instead of a bare Git or IO error. Read-only: never closes, kills or signals a process. Only meant for the failure path;
/// callers must not run it before every delete.
/// </summary>
public interface IFileLockInspector
{
    /// <summary>
    /// Looks for processes that have a file under <paramref name="path"/> open or whose current directory is inside it.
    /// Never throws for an inspection problem (missing path, access denied, unsupported platform); those come back as an
    /// empty or partial result with <see cref="FileLockInspectionResult.MayBeIncomplete"/> set.
    /// </summary>
    Task<FileLockInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default);
}
