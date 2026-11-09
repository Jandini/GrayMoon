namespace GrayMoon.Worker.Tests;

/// <summary>
/// Cleanup for the throwaway directories tests create. A plain <c>Directory.Delete</c> inside a swallowing
/// <c>try/catch</c> silently leaves a repository behind: git writes its object files read-only, which blocks the
/// delete on Windows, and a path past 260 characters blocks it too. Every test run used to leak thousands of
/// directories into the temp folder that way. This clears the read-only flags, reaches long paths, and retries a
/// moment later because a just-stopped process or watcher can still hold a handle.
/// </summary>
internal static class TestDirectory
{
    /// <summary>Creates a new empty directory in the temp folder whose name starts with <c>graymoon-{label}-</c>.</summary>
    public static string Create(string label) => Directory.CreateTempSubdirectory($"graymoon-{label}-").FullName;

    /// <summary>Deletes <paramref name="path"/> and everything under it. Never throws: cleanup must not fail a test.</summary>
    public static void Delete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        var root = OperatingSystem.IsWindows() && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? @"\\?\" + Path.GetFullPath(path)
            : path;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(root))
                    return;

                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }
}
