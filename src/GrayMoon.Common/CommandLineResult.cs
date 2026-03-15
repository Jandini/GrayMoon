namespace GrayMoon.Common;

/// <summary>
/// Result of running a process via <see cref="ICommandLineService"/>. ExitCode is -1 when the process failed to start.
/// </summary>
public record CommandLineResult(int ExitCode, string? Stdout, string? Stderr);
