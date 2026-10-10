using GrayMoon.App.Components.Features;

namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Which Open-in tools are installed on this machine. Desktop mode renders the recent-tool
/// buttons on the first paint, before the WebView posts its own detection result.
/// Keep the checks aligned with GrayMoon.Desktop's ToolAvailabilityService.
/// </summary>
internal static class InstalledOpenInTools
{
    private static readonly object Gate = new();
    private static IReadOnlySet<string>? _cached;

    /// <summary>
    /// One check per <see cref="FeatureOpenInTools.All"/> tool that needs an install, keyed by tool id.
    /// A CLI counts as installed only when it is on PATH, because Desktop launches it by name.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, Func<bool>> Checks = new Dictionary<string, Func<bool>>(StringComparer.Ordinal)
    {
        [FeatureOpenInTools.Cursor] = () => IsOnPath("cursor.cmd", "cursor.exe") || ExistsUnderLocalAppData(@"Programs\cursor\Cursor.exe"),
        [FeatureOpenInTools.ClaudeCli] = () => IsOnPath("claude.cmd", "claude.exe", "claude.ps1"),
        [FeatureOpenInTools.CodexCli] = () => IsOnPath("codex.cmd", "codex.exe", "codex.ps1"),
        [FeatureOpenInTools.VsCode] = () => IsOnPath("code.cmd", "code.exe") || ExistsUnderLocalAppData(@"Programs\Microsoft VS Code\Code.exe"),
        [FeatureOpenInTools.VisualStudio] = FindVisualStudio,
    };

    /// <summary>Ids of the installed tools. Detected once per process.</summary>
    public static IReadOnlySet<string> Detect()
    {
        lock (Gate)
        {
            return _cached ??= DetectCore();
        }
    }

    private static HashSet<string> DetectCore()
    {
        var installed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (toolId, check) in Checks)
        {
            if (check())
                installed.Add(toolId);
        }

        return installed;
    }

    private static bool IsOnPath(params string[] fileNames)
    {
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                foreach (var fileName in fileNames)
                {
                    if (File.Exists(Path.Combine(dir.Trim().Trim('"'), fileName)))
                        return true;
                }
            }
        }
        catch
        {
            // Best-effort detection only.
        }

        return false;
    }

    private static bool ExistsUnderLocalAppData(string relativePath)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return !string.IsNullOrEmpty(localAppData) && File.Exists(Path.Combine(localAppData, relativePath));
        }
        catch
        {
            return false;
        }
    }

    private static bool FindVisualStudio()
    {
        try
        {
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var vswhere = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (File.Exists(vswhere))
                return true;

            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         programFilesX86
                     })
            {
                var vsRoot = Path.Combine(root, "Microsoft Visual Studio");
                if (Directory.Exists(vsRoot) &&
                    Directory.EnumerateFiles(vsRoot, "devenv.exe", SearchOption.AllDirectories).Any())
                {
                    return true;
                }
            }
        }
        catch
        {
            // Best-effort detection only.
        }

        return false;
    }
}
