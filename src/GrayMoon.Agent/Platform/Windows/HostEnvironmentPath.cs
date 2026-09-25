using System.Runtime.Versioning;

namespace GrayMoon.Agent.Platform.Windows;

/// <summary>
/// Rebuilds the process PATH from the current machine/user environment so a newly started
/// Agent (especially as a Windows service) does not keep a stale Service Control Manager snapshot.
/// Does not mutate permanent machine or user environment variables.
/// </summary>
public static class HostEnvironmentPath
{
    /// <summary>
    /// Merges machine, user, and process PATH segments with case-insensitive de-duplication,
    /// preserving first-seen order. Optionally appends the user's .dotnet\tools directory once.
    /// </summary>
    public static string MergePath(
        string? machinePath,
        string? userPath,
        string? processPath,
        string? dotnetToolsDirectory)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AppendPath(machinePath, result, seen);
        AppendPath(userPath, result, seen);
        AppendPath(processPath, result, seen);
        AppendSegment(dotnetToolsDirectory, result, seen);

        return string.Join(';', result);
    }

    /// <summary>
    /// Reads the current machine and user PATH values, merges them with the existing process PATH,
    /// and sets the process-level PATH. Windows only.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void RefreshProcessPath()
    {
        var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
        var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        var process = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);

        string? toolsDir = null;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var candidate = Path.Combine(profile, ".dotnet", "tools");
            if (Directory.Exists(candidate))
                toolsDir = candidate;
        }

        var merged = MergePath(machine, user, process, toolsDir);
        Environment.SetEnvironmentVariable("PATH", merged, EnvironmentVariableTarget.Process);
    }

    private static void AppendPath(string? path, List<string> result, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        foreach (var part in path.Split(';', StringSplitOptions.None))
            AppendSegment(part, result, seen);
    }

    private static void AppendSegment(string? segment, List<string> result, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return;

        var trimmed = segment.Trim();
        if (trimmed.Length == 0)
            return;

        if (!seen.Add(trimmed))
            return;

        result.Add(trimmed);
    }
}
