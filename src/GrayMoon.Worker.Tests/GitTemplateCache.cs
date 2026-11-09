using System.Collections.Concurrent;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Builds a git scenario once per test run and hands every test its own private copy. Starting git is the dominant
/// cost of this suite (each process is tens of milliseconds, far more with a virus scanner watching), and most tests
/// spend their first several processes on identical setup: init, configure an identity, make a first commit, seed and
/// push to a bare remote. Copying a few small files is thousands of times cheaper than running that again, and each
/// test still owns a fully isolated directory it can mutate freely.
/// </summary>
/// <remarks>
/// A template must not contain absolute paths that point back into itself, because the copy lives elsewhere. A
/// scenario with such a path (a remote URL, for example) repairs it after copying.
/// </remarks>
internal static class GitTemplateCache
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var root = Directory.CreateTempSubdirectory("graymoon-git-templates-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteTree(root);
        return root;
    });

    private static readonly ConcurrentDictionary<string, Lazy<string>> Templates = new();

    /// <summary>Copies the template named <paramref name="key"/> into <paramref name="destination"/>, building it first if no test has needed it yet.</summary>
    /// <param name="build">Fills an empty directory with the scenario. Runs at most once per key per test run.</param>
    public static void CopyTo(string key, string destination, Action<string> build)
    {
        var template = Templates.GetOrAdd(key, k => new Lazy<string>(() =>
        {
            var path = Path.Combine(Root.Value, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            build(path);
            return path;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        CopyDirectory(template, destination);
        Rebase(destination, template);
    }

    /// <summary>
    /// A cloned or seeded repository records the path of its remote in <c>config</c>. After the copy that path still
    /// names the template, so a test would push into the shared template. Every config file is rewritten to name the
    /// test's own copy instead, at no cost in git processes (unlike <c>git remote set-url</c>).
    /// </summary>
    private static void Rebase(string destination, string template)
    {
        foreach (var config in Directory.EnumerateFiles(destination, "config", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(config);
            if (!text.Contains("url", StringComparison.Ordinal))
                continue;

            // git writes a Windows path with either separator, and doubles a backslash inside a config value.
            var rewritten = text
                .Replace(template.Replace("\\", "\\\\"), destination.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase)
                .Replace(template, destination, StringComparison.OrdinalIgnoreCase)
                .Replace(template.Replace('\\', '/'), destination.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
            if (rewritten != text)
                File.WriteAllText(config, rewritten);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);

            // Git writes object files read-only, which would block the test's own cleanup.
            File.SetAttributes(target, FileAttributes.Normal);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void DeleteTree(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
