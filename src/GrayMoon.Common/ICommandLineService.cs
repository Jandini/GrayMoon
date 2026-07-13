namespace GrayMoon.Common;

/// <summary>
/// Centralized process execution with safe logging. Use for all command-line invocations so logging stays in one place.
/// </summary>
public interface ICommandLineService
{
    /// <summary>
    /// Runs the executable with the given arguments. Optionally sets working directory and feeds stdin.
    /// Logs once at DEBUG after completion (safe parameters, elapsed ms, exit code). Tokens are redacted.
    /// Per-line stdout/stderr DEBUG logs are omitted for git diff and git show file-content commands.
    /// </summary>
    /// <param name="fileName">Executable name or path (e.g. "git", "dotnet").</param>
    /// <param name="arguments">Command-line arguments. Pass null or empty if none.</param>
    /// <param name="workingDirectory">Working directory; null or empty to use current.</param>
    /// <param name="stdin">Optional stdin content; null to not redirect stdin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="streamStderrAsStdout">
    /// When true, process stderr is still returned on the result as stderr, but live overlay streaming uses stdout styling
    /// (git/dotnet-gitversion use stderr for progress and non-fatal messages).
    /// </param>
    /// <param name="mirrorFailureOutputAsStderr">
    /// When true and exit code is non-zero, combined stdout+stderr is sent to the overlay again as stderr lines (red).
    /// </param>
    /// <returns>Exit code (or -1 if process failed to start), stdout, and stderr.</returns>
    Task<CommandLineResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        string? stdin = null,
        CancellationToken cancellationToken = default,
        bool streamStderrAsStdout = false,
        bool mirrorFailureOutputAsStderr = false);

    /// <summary>
    /// Runs the executable with an explicit argument list instead of a single argument string - no shell
    /// quoting/escaping is applied, so this is the safe path for arguments built from untrusted input
    /// (e.g. repository-relative file paths). <paramref name="stdinBytes"/> is written to the process's
    /// raw stdin stream verbatim (not re-encoded), so callers control the exact bytes sent - required for
    /// NUL-delimited UTF-8 pathspec input.
    /// </summary>
    Task<CommandLineResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        byte[]? stdinBytes = null,
        CancellationToken cancellationToken = default,
        bool streamStderrAsStdout = false,
        bool mirrorFailureOutputAsStderr = false);
}
