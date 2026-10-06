using System.Text.RegularExpressions;

namespace GrayMoon.App.Tests;

/// <summary>
/// Guards the Workspace-repository Worker path contract: every App send site that passes
/// <c>workspaceRoot</c> together with <c>repositoryName</c>/<c>repositoryNames</c> must also pass
/// <c>workspaceRepositoryName</c>, otherwise the Worker resolves the Workspace-role repository to a subfolder.
/// </summary>
public sealed class WorkerSendSiteGuardTests
{
    /// <summary>
    /// Repo-relative paths (forward slashes) whose offending blocks are knowingly left for the unit that owns the file.
    /// Expected to stay empty except for files owned by a later unit.
    /// </summary>
    private static readonly string[] AllowedFiles = [];

    private static readonly Regex AnonymousObject = new(@"new\s*\{[^}]*\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RepositoryNameWord = new(@"\brepositoryNames?\b", RegexOptions.Compiled);

    [Fact]
    public void Every_send_site_with_workspace_root_and_repository_name_also_sends_workspace_repository_name()
    {
        var repoRoot = FindRepoRoot();
        var appRoot = Path.Combine(repoRoot, "src", "GrayMoon.App");
        var offenders = new List<string>();
        var inspected = 0;

        var files = Directory.EnumerateFiles(appRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsBuildOutput(f, appRoot));

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (AllowedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(file);
            foreach (Match block in AnonymousObject.Matches(text))
            {
                var value = block.Value;
                if (!value.Contains("workspaceRoot", StringComparison.Ordinal) || !RepositoryNameWord.IsMatch(value))
                    continue;

                inspected++;
                if (value.Contains("workspaceRepositoryName", StringComparison.Ordinal))
                    continue;

                var line = 1 + text.Take(block.Index).Count(c => c == '\n');
                offenders.Add($"{relative}:{line}");
            }
        }

        Assert.True(inspected > 0, "The guard found no send sites at all; the scan is broken.");
        Assert.True(offenders.Count == 0,
            "Send sites with workspaceRoot and repositoryName but no workspaceRepositoryName:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    private static bool IsBuildOutput(string file, string appRoot)
    {
        var relative = Path.GetRelativePath(appRoot, file).Replace('\\', '/');
        return relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GrayMoon.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("GrayMoon.slnx was not found above " + AppContext.BaseDirectory);
    }
}
