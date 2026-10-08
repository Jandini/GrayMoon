using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Services;

/// <summary>Used where no lock inspection is implemented (Linux, macOS): callers fall back to the original failure message.</summary>
public sealed class UnsupportedFileLockInspector : IFileLockInspector
{
    public Task<FileLockInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(FileLockInspectionResult.Unsupported("Finding the programs that use a folder is only supported on Windows."));
}
