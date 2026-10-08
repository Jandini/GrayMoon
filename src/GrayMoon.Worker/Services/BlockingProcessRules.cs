using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Platform-neutral rules for blocking processes: which ones GrayMoon must never end, and how a found path is attributed to the
/// most specific of several inspected folders.
/// </summary>
internal static class BlockingProcessRules
{
    private static readonly HashSet<string> SystemProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Secure System", "Registry", "Memory Compression", "MemCompression", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "LsaIso", "dwm", "fontdrvhost",
    };

    /// <summary>True for core Windows processes, by id or name.</summary>
    internal static bool IsSystemProcess(int processId, string? processName) =>
        processId is 0 or 4 || (processName is not null && SystemProcessNames.Contains(processName));

    /// <summary>True for the Worker, the App and GrayMoon Desktop (by process or executable name).</summary>
    internal static bool IsGrayMoonProcess(string? processName, string? executablePath)
    {
        static bool StartsWithGrayMoon(string? value) =>
            !string.IsNullOrWhiteSpace(value) && value.StartsWith("graymoon", StringComparison.OrdinalIgnoreCase);

        return StartsWithGrayMoon(processName)
            || StartsWithGrayMoon(string.IsNullOrWhiteSpace(executablePath) ? null : Path.GetFileName(executablePath));
    }

    /// <summary>
    /// The <see cref="BlockingProcessProtectedReason"/> that forbids ending <paramref name="process"/> regardless of access
    /// rights, or null when only access rights decide.
    /// </summary>
    internal static string? FixedProtection(BlockingProcessInfo process)
    {
        if (IsGrayMoonProcess(process.ProcessName, process.ExecutablePath))
            return BlockingProcessProtectedReason.GrayMoon;

        return process.Kind switch
        {
            BlockingProcessKind.Critical => BlockingProcessProtectedReason.System,
            BlockingProcessKind.Service => BlockingProcessProtectedReason.Service,
            BlockingProcessKind.Explorer => BlockingProcessProtectedReason.Explorer,
            _ => IsSystemProcess(process.ProcessId, process.ProcessName) ? BlockingProcessProtectedReason.System : null,
        };
    }

    /// <summary>Applies <paramref name="protectedReason"/> (null = may be ended) to <paramref name="process"/>.</summary>
    internal static BlockingProcessInfo WithProtection(BlockingProcessInfo process, string? protectedReason) =>
        process with { CanTerminate = protectedReason is null, ProtectedReason = protectedReason };

    /// <summary>
    /// Index of the root in <paramref name="roots"/> that most specifically contains <paramref name="path"/> (the longest
    /// matching root), or -1. Each root may have several spellings (as requested, and with links resolved).
    /// </summary>
    internal static int MostSpecificRoot(IReadOnlyList<IReadOnlyList<string>> roots, string path)
    {
        var best = -1;
        var bestLength = -1;
        for (var i = 0; i < roots.Count; i++)
        {
            foreach (var form in roots[i])
            {
                if (form.Length > bestLength && IsSameOrUnder(path, form))
                {
                    best = i;
                    bestLength = form.Length;
                }
            }
        }

        return best;
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or below it (case-insensitive, trailing separators ignored).</summary>
    internal static bool IsSameOrUnder(string candidate, string root)
    {
        var c = candidate.TrimEnd('\\', '/');
        var r = root.TrimEnd('\\', '/');
        if (r.Length == 0 || c.Length < r.Length || !c.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            return false;

        return c.Length == r.Length || c[r.Length] is '\\' or '/';
    }
}
