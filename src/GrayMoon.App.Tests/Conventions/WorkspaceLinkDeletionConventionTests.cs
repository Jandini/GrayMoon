using System.Text.RegularExpressions;

namespace GrayMoon.App.Tests.Conventions;

/// <summary>
/// The Workspace link rows are the only record of which folders GrayMoon manages. Before one is deleted its folder
/// has to be detached (the Worker releases its file watchers and removes GrayMoon's Git hooks) through
/// <c>WorkspaceRepositoryDetacher</c>. A new code path that deletes a Workspace or a Workspace link somewhere else
/// would leave hooks behind in a folder nobody remembers, so the places that may delete are listed here.
/// </summary>
public sealed partial class WorkspaceLinkDeletionConventionTests
{
    /// <summary>File name, and why it may delete.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WorkspaceRepository.cs"] = "ReplaceRepositoriesCoreAsync: reached only through IWorkspaceRepositoryOperations.SaveWorkspaceAsync, which detaches the leaving repositories first.",
        ["WorkspaceRepositoryOperations.cs"] = "Disable, moved Source, link undo and restore rollback: each calls the detacher before its delete.",
        ["WorkspaceRepositoryOperations.Removal.cs"] = "Remove Workspace: deletes the row inside the detacher's whileDetached callback.",
    };

    [Fact]
    public void A_workspace_or_a_workspace_link_is_deleted_only_where_the_folder_is_detached_first()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var name = Path.GetFileName(file);
            if (Allowed.ContainsKey(name))
                continue;

            var text = File.ReadAllText(file);
            foreach (var statement in text.Split(';'))
            {
                if (DeletesWorkspaceRows().IsMatch(statement))
                    offenders.Add($"{Path.GetRelativePath(ConventionSource.RepositoryRoot(), file)}: {Collapse(statement)}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Workspace or Workspace link rows are deleted outside the classes that detach the folder first. "
            + "Go through IWorkspaceRepositoryOperations (RemoveWorkspaceAsync / SaveWorkspaceAsync / DisableWorkspaceRepositoryAsync), "
            + "or call WorkspaceRepositoryDetacher before the delete and add the file to this test with the reason:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void Every_allowed_file_still_exists_and_still_names_the_detacher_or_is_reached_through_it()
    {
        var files = SourceFiles().ToDictionary(Path.GetFileName, f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var name in Allowed.Keys)
            Assert.True(files.ContainsKey(name), $"{name} is on the allow-list but no longer exists; remove the entry.");

        // The two operations files call the detacher themselves.
        Assert.Contains("detacher.", File.ReadAllText(files["WorkspaceRepositoryOperations.cs"]!));
        Assert.Contains("detacher.DetachAsync", File.ReadAllText(files["WorkspaceRepositoryOperations.Removal.cs"]!));
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(ConventionSource.AppDirectory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string Collapse(string statement)
    {
        var text = Whitespace().Replace(statement, " ").Trim();
        return text.Length > 160 ? text[^160..] : text;
    }

    // db.Workspaces / db.WorkspaceRepositories followed, in the same statement, by a row delete.
    [GeneratedRegex(@"\.(Workspaces|WorkspaceRepositories)\b(?![A-Za-z])[\s\S]*?\.(Remove|RemoveRange|ExecuteDeleteAsync)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex DeletesWorkspaceRows();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
