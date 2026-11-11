using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace GrayMoon.Worker.Tests.Conventions;

/// <summary>
/// Tests of one assembly run in parallel in one process, so a test must not change what the whole process sees:
/// an environment variable or the current directory. A test that set <c>GIT_CONFIG_COUNT</c> for a moment made
/// every git process started by another test in that moment exit 128, which failed an unrelated test now and then.
/// Hand the value to the code under test instead (a parameter, or <c>GitProcessEnvironmentScope</c> for git).
/// </summary>
public sealed class TestProcessStateConventionTests
{
    private static readonly Regex ProcessWideWrite = new(
        @"\bEnvironment\s*\.\s*SetEnvironmentVariable\s*\(|\bDirectory\s*\.\s*SetCurrentDirectory\s*\(|\bEnvironment\s*\.\s*CurrentDirectory\s*=[^=]",
        RegexOptions.Compiled);

    [Fact]
    public void No_test_changes_the_environment_or_the_current_directory_of_the_test_process()
    {
        var files = TestSources();
        Assert.True(files.Count > 100, "The scan found almost no test files; it is broken.");

        var violations = files
            .SelectMany(f => FindWrites(f.Text).Select(line => $"{f.File}({line})"))
            .ToList();

        Assert.True(violations.Count == 0,
            "A test must not set an environment variable or the current directory of the test process; parallel tests share them:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Scan_reports_a_write_and_ignores_a_read()
    {
        const string source = """
            var previous = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            // Environment.SetEnvironmentVariable("COMMENTED", "1");
            if (Environment.CurrentDirectory == root) return;
            Directory.SetCurrentDirectory(root);
            Environment.CurrentDirectory = root;
            """;

        Assert.Equal([2, 5, 6], FindWrites(source));
    }

    /// <summary>The 1-based lines of <paramref name="source"/> that write process-wide state. Line comments are ignored.</summary>
    internal static IReadOnlyList<int> FindWrites(string source)
    {
        source = Regex.Replace(source, @"(?<![:""'/\w])//[^\r\n]*", m => new string(' ', m.Length));
        return ProcessWideWrite.Matches(source)
            .Select(m => source.AsSpan(0, m.Index).Count('\n') + 1)
            .ToList();
    }

    private static IReadOnlyList<(string File, string Text)> TestSources([CallerFilePath] string thisFile = "")
    {
        // The build output can live outside the repository (--artifacts-path), so fall back to this source file.
        foreach (var origin in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile)! })
        {
            var dir = new DirectoryInfo(origin);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GrayMoon.slnx")))
                dir = dir.Parent;
            if (dir == null)
                continue;

            var src = Path.Combine(dir.FullName, "src");
            var separator = Path.DirectorySeparatorChar;
            return Directory.EnumerateDirectories(src, "*.Tests")
                .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
                    && !f.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
                // This file holds the sample the scan is proven with.
                .Where(f => !string.Equals(Path.GetFileName(f), Path.GetFileName(thisFile), StringComparison.OrdinalIgnoreCase))
                .Select(f => (Path.GetRelativePath(src, f).Replace('\\', '/'), File.ReadAllText(f)))
                .ToList();
        }

        throw new InvalidOperationException("GrayMoon.slnx was not found above the test output or the test source.");
    }
}
