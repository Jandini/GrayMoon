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

    public const string PreflightTitle = "Programs are using this Feature";

    public const string PreflightBody =
        "These programs have files or folders open inside the Feature. GrayMoon cannot delete what they hold.";

    public const string Checking = "Checking for programs using this Feature...";

    public const string CheckFailedTitle = "GrayMoon could not check this Feature";

    public const string CheckFailedBody =
        "GrayMoon could not find out which programs are using this Feature. Continue may leave files behind if a program is " +
        "still using it.";

    public const string KillExplanation = "Kill ends the selected programs immediately. Unsaved work in them is lost.";

    public const string ContinueExplanation =
        "Continue removes the Feature without closing them. Files they hold will be left on disk; you can delete them later " +
        "from the removal report.";

    public const string LeftoverKillExplanation =
        "Kill ends the selected programs immediately (unsaved work in them is lost), then deletes the leftover files again.";

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

        if (process.IsLoadedModule)
            return "Runs a program or library from this Feature";

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

    /// <summary>True when the lookup failed for any folder, so the dialog cannot say whether something is using the Feature.</summary>
    public static bool AnyLookupFailed(IEnumerable<RemoveFeatureRepositoryBlockers>? blockers) =>
        blockers?.Any(b => b.LookupFailed) == true;

    /// <summary>
    /// True when Remove should stop at the "Programs are using this Feature" step: something is using a folder, or the check
    /// could not run (null result, or a failed lookup).
    /// </summary>
    public static bool NeedsBlockerStep(IReadOnlyList<RemoveFeatureRepositoryBlockers>? blockers) =>
        blockers is null || AnyLookupFailed(blockers) || AnyProcesses(blockers);

    /// <summary>Every process shown for the folders that still exist, one entry per process id.</summary>
    public static IReadOnlyList<RemoveFeatureBlockingProcess> ShownProcesses(IEnumerable<RemoveFeatureRepositoryBlockers>? blockers) =>
        Ordered(blockers?.Where(b => b.PathExists).SelectMany(b => b.Processes));

    /// <summary>The default selection for Kill: every listed process GrayMoon is allowed to end.</summary>
    public static IReadOnlySet<int> DefaultSelection(IEnumerable<RemoveFeatureBlockingProcess>? processes) =>
        (processes ?? []).Where(p => p.CanTerminate).Select(p => p.ProcessId).ToHashSet();

    /// <summary>The selected, endable processes with the start time the lookup reported (sent back so a reused id is never ended).</summary>
    public static IReadOnlyList<RemoveFeatureProcessSelection> Selections(
        IEnumerable<RemoveFeatureBlockingProcess>? processes,
        IReadOnlySet<int> selected) =>
        Ordered(processes)
            .Where(p => p.CanTerminate && selected.Contains(p.ProcessId))
            .Select(p => new RemoveFeatureProcessSelection(p.ProcessId, p.StartTimeUtc))
            .ToList();

    /// <summary>Why GrayMoon will not end a process (shown instead of its checkbox).</summary>
    public static string ProtectedText(string? protectedReason) => protectedReason switch
    {
        "System" => "A system process. GrayMoon cannot end it.",
        "Service" => "A Windows service. GrayMoon does not end services; stop it yourself if needed.",
        "Explorer" => "File Explorer. Close the window that shows this Feature yourself.",
        "GrayMoon" => "Part of GrayMoon. GrayMoon does not end itself.",
        "AccessDenied" => "GrayMoon is not allowed to end it (it may run as administrator). Close it yourself.",
        _ => "GrayMoon cannot end it. Close it yourself.",
    };

    /// <summary>What happened to a process the user asked to end; null when it was ended (nothing more to say).</summary>
    public static string? OutcomeText(string? outcome) => outcome switch
    {
        RemoveFeatureProcessOutcome.Killed => null,
        RemoveFeatureProcessOutcome.AlreadyExited => null,
        RemoveFeatureProcessOutcome.NotHoldingAnymore => null,
        RemoveFeatureProcessOutcome.StartTimeChanged => "Not ended: it restarted since the check. Check the list and try again.",
        RemoveFeatureProcessOutcome.Protected => "Not ended: GrayMoon does not end this kind of program.",
        RemoveFeatureProcessOutcome.AccessDenied => "Not ended: access denied. Close it yourself.",
        _ => "Could not be ended. Close it yourself.",
    };

    /// <summary>One line summing up a Kill, e.g. "Ended 2 programs."; null when nothing was ended.</summary>
    public static string? KillSummary(IEnumerable<RemoveFeatureProcessOutcome>? outcomes)
    {
        var ended = outcomes?.Count(o => o.Outcome == RemoveFeatureProcessOutcome.Killed) ?? 0;
        return ended switch
        {
            0 => null,
            1 => "Ended 1 program.",
            _ => $"Ended {ended} programs.",
        };
    }

    private static string? NameFromPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);
}
