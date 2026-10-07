using GrayMoon.Application;

namespace GrayMoon.App.Components.Features;

/// <summary>
/// Plain-language text for the processes that keep a Feature worktree in use (Remove Feature dialog). Pure, so the
/// wording and the empty-list fallback are unit tested without rendering.
/// </summary>
public static class RemoveFeatureBlockerText
{
    public const string InUseTitle = "Feature is still in use";

    public const string InUseBody =
        "GrayMoon could not remove the Feature because one or more processes are using files in its worktree.";

    public const string LeftoverBody =
        "The Feature was removed, but some of its files could not be deleted because they are still in use.";

    public const string CloseAndRetry = "Close these programs, then select Retry. GrayMoon does not close them for you.";

    /// <summary>Shown when the Worker found no blocker (it may have exited already, or it is a kind Windows cannot report).</summary>
    public const string NoBlockerFound =
        "GrayMoon could not identify the program using this folder. Close terminals, editors, AI tools and file windows " +
        "that use this Feature, wait a moment, then select Retry.";

    /// <summary>Main line for a process, e.g. "Claude (PID 18472)".</summary>
    public static string Title(RemoveFeatureBlockingProcess process)
    {
        var name = string.IsNullOrWhiteSpace(process.ProcessName)
            ? NameFromPath(process.ExecutablePath) ?? "Unknown program"
            : process.ProcessName.Trim();
        return $"{name} (PID {process.ProcessId})";
    }

    /// <summary>Why this process blocks removal, in plain words.</summary>
    public static string Reason(RemoveFeatureBlockingProcess process)
    {
        if (process.IsWorkingDirectory)
            return "Its current folder is inside this Feature";

        return process.Kind switch
        {
            "Service" => string.IsNullOrWhiteSpace(process.ServiceName)
                ? "A Windows service has files open in this Feature"
                : $"Windows service {process.ServiceName} has files open in this Feature",
            "Explorer" => "A File Explorer window or preview has files open in this Feature",
            "Critical" => "A system process has files open in this Feature",
            _ => "Has files open in this Feature"
        };
    }

    /// <summary>The executable path when it adds something beyond the name; null otherwise.</summary>
    public static string? Detail(RemoveFeatureBlockingProcess process) =>
        string.IsNullOrWhiteSpace(process.ExecutablePath) ? null : process.ExecutablePath;

    /// <summary>
    /// Blockers ordered for display: one entry per process id, programs whose folder is inside the Feature first
    /// (closing a shell or an AI tool usually also closes its child processes), then by name.
    /// </summary>
    public static IReadOnlyList<RemoveFeatureBlockingProcess> Ordered(IEnumerable<RemoveFeatureBlockingProcess>? processes) =>
        (processes ?? [])
            .GroupBy(p => p.ProcessId)
            .Select(g => g.First())
            .OrderBy(p => p.IsWorkingDirectory ? 0 : 1)
            .ThenBy(p => p.ProcessName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ProcessId)
            .ToList();

    /// <summary>True when any repository reported at least one blocking process.</summary>
    public static bool AnyProcesses(IEnumerable<RemoveFeatureRepositoryBlockers>? blockers) =>
        blockers?.Any(b => b.PathExists && b.Processes.Count > 0) == true;

    private static string? NameFromPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);
}
