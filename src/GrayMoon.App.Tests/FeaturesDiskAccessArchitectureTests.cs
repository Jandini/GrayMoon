namespace GrayMoon.App.Tests;

/// <summary>
/// The App never touches the developer's disk directly: disk facts about repository and worktree
/// paths come only from the Agent through IAgentBridge.SendCommandAsync, because the App can run in
/// Docker where those paths do not exist. This scans App/Services/Features/ source files and fails
/// if any of them call System.IO.Directory, System.IO.File, or Path.Exists. WorkspaceContextPathResolver.cs
/// is allow-listed because it only builds path strings and never reads disk.
/// </summary>
public sealed class FeaturesDiskAccessArchitectureTests
{
    private static readonly string[] BannedPatterns = ["Directory.", "File.", "Path.Exists("];
    private const string AllowListedFile = "WorkspaceContextPathResolver.cs";

    [Fact]
    public void Features_source_files_never_call_disk_apis_directly()
    {
        var featuresDir = FindFeaturesSourceDirectory();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(featuresDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, AllowListedFile, StringComparison.OrdinalIgnoreCase))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                foreach (var pattern in BannedPatterns)
                {
                    if (line.Contains(pattern, StringComparison.Ordinal))
                        violations.Add($"{fileName}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "App/Services/Features must not touch disk directly; disk facts come from the Agent via InspectWorktree:\n"
            + string.Join(Environment.NewLine, violations));
    }

    private static string FindFeaturesSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GrayMoon.slnx")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Could not locate GrayMoon.slnx above the test output directory.");

        var featuresDir = Path.Combine(dir.FullName, "src", "GrayMoon.App", "Services", "Features");
        if (!Directory.Exists(featuresDir))
            throw new InvalidOperationException($"Expected Features source directory at '{featuresDir}'.");

        return featuresDir;
    }
}
