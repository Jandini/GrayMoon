namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Which Open-in tools are installed on this machine. Desktop mode renders the recent-tool
/// buttons on the first paint, before the WebView posts its own detection result.
/// Keep the checks aligned with GrayMoon.Desktop's ToolAvailabilityService.
/// </summary>
internal static class InstalledOpenInTools
{
    private static readonly object Gate = new();
    private static Snapshot? _cached;

    internal readonly record struct Snapshot(bool Cursor, bool VsCode, bool VisualStudio, bool ClaudeCli);

    public static Snapshot Detect()
    {
        lock (Gate)
        {
            return _cached ??= DetectCore();
        }
    }

    private static Snapshot DetectCore() => new(
        Cursor: IsOnPath("cursor.cmd") || IsOnPath("cursor.exe") || ExistsUnderLocalAppData(@"Programs\cursor\Cursor.exe"),
        VsCode: IsOnPath("code.cmd") || IsOnPath("code.exe") || ExistsUnderLocalAppData(@"Programs\Microsoft VS Code\Code.exe"),
        VisualStudio: FindVisualStudio(),
        ClaudeCli: IsOnPath("claude.cmd") || IsOnPath("claude.exe") || IsOnPath("claude.ps1"));

    private static bool IsOnPath(string fileName)
    {
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                var candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
                if (File.Exists(candidate))
                    return true;
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
