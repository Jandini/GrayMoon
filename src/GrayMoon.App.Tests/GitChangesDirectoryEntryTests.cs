using System.Diagnostics;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Tests;

/// <summary>
/// Git Changes must never render a directory as a file. <c>git status --untracked-files=all</c> reports an untracked
/// folder it does not descend into (a nested Git repository that is not ignored, e.g. <c>GrayMoon.Release/</c> under a
/// Workspace repository) as one entry with a trailing "/". The tree turns that into an explicit directory row.
/// </summary>
public sealed class GitChangesDirectoryEntryTests
{
    private static WorkspaceGitChangeEntryView Untracked(string path) => new()
    {
        Path = path,
        WorktreeChange = GitChangeKind.Untracked,
    };

    private static WorkspaceGitChangeEntryView Modified(string path) => new()
    {
        Path = path,
        WorktreeChange = GitChangeKind.Modified,
        IsTracked = true,
    };

    private static IReadOnlyList<GitChangesTreeRow> Build(params WorkspaceGitChangeEntryView[] changes) =>
        GitChangesTreeBuilder.Build(
            new WorkspaceGitChangesView
            {
                WorkspaceId = 1,
                Repositories =
                [
                    new WorkspaceGitChangesRepositoryView
                    {
                        WorkspaceRepositoryId = 1,
                        RepositoryId = 1,
                        RepositoryName = "Workspace",
                        Changes = changes,
                    },
                ],
            },
            filterQuery: null);

    [Fact]
    public void Nested_file_creates_parent_directory_nodes()
    {
        var rows = Build(Untracked(".claude/skills/setup/SKILL.md"));

        var folders = rows.Where(r => r.Kind == GitChangesTreeRowKind.Folder).ToList();
        Assert.Equal([".claude", "skills", "setup"], folders.Select(f => f.Label));
        Assert.All(folders, f => Assert.False(f.IsDirectoryEntry));
        var file = Assert.Single(rows, r => r.Kind == GitChangesTreeRowKind.File);
        Assert.Equal("SKILL.md", file.Label);
        Assert.Equal(folders[^1].Depth + 1, file.Depth);
    }

    [Fact]
    public void Directory_entry_with_the_name_of_a_repository_is_rendered_as_a_directory_not_a_file()
    {
        var rows = Build(Untracked("GrayMoon.Release/"), Untracked("GrayMoon.wiki/"));

        Assert.DoesNotContain(rows, r => r.Kind == GitChangesTreeRowKind.File);
        var directories = rows.Where(r => r.IsDirectoryEntry).ToList();
        Assert.Equal(["GrayMoon.Release", "GrayMoon.wiki"], directories.Select(d => d.Label));
        Assert.All(directories, d =>
        {
            Assert.Equal(GitChangesTreeRowKind.Folder, d.Kind);
            Assert.False(d.HasChildren);
            Assert.EndsWith("/", d.FilePath);
        });
    }

    [Fact]
    public void Parent_directory_node_is_not_replaced_by_a_file_or_directory_entry_of_the_same_name()
    {
        var rows = Build(Untracked("Child/"), Untracked("Child/notes.txt"));

        var child = Assert.Single(rows, r => r.Label == "Child");
        Assert.Equal(GitChangesTreeRowKind.Folder, child.Kind);
        Assert.False(child.IsDirectoryEntry);
        Assert.True(child.HasChildren);
        Assert.Equal("notes.txt", Assert.Single(rows, r => r.Kind == GitChangesTreeRowKind.File).Label);
    }

    [Fact]
    public void Tracked_files_beside_repository_directories_are_shown_as_files_and_folders_sort_first()
    {
        var rows = Build(Modified("CLAUDE.md"), Untracked("GrayMoon.Release/"), Modified(".gitignore"), Modified(".claude/settings.json"));

        var underRepo = rows.Where(r => r.Depth == 2).ToList();
        Assert.Equal([".claude", "GrayMoon.Release", ".gitignore", "CLAUDE.md"], underRepo.Select(r => r.Label));
        Assert.Equal(GitChangesTreeRowKind.Folder, underRepo[0].Kind);
        Assert.False(underRepo[0].IsDirectoryEntry);
        Assert.True(underRepo[1].IsDirectoryEntry);
        Assert.Equal(GitChangesTreeRowKind.File, underRepo[2].Kind);
        Assert.Equal(GitChangesTreeRowKind.File, underRepo[3].Kind);
    }

    [Fact]
    public void Nested_directory_entry_sits_under_its_parent_folder()
    {
        var rows = Build(Untracked("tools/vendor-repo/"));

        var tools = Assert.Single(rows, r => r.Label == "tools");
        Assert.False(tools.IsDirectoryEntry);
        var vendor = Assert.Single(rows, r => r.Label == "vendor-repo");
        Assert.True(vendor.IsDirectoryEntry);
        Assert.Equal(tools.Depth + 1, vendor.Depth);
        Assert.Equal("tools/vendor-repo/", vendor.FilePath);
    }

    [Fact]
    public void Directory_entry_folder_path_round_trips_for_copy_path()
    {
        var rows = Build(Untracked("GrayMoon.Release/"));

        var directory = Assert.Single(rows, r => r.IsDirectoryEntry);
        Assert.Equal("GrayMoon.Release", GitChangesTreeBuilder.FolderRelativePathOf(directory));
    }

    [Theory]
    [InlineData("GrayMoon.Release/", true)]
    [InlineData("a/b/", true)]
    [InlineData("README.md", false)]
    [InlineData("a/b.txt", false)]
    public void Directory_paths_are_recognized_by_the_trailing_slash(string path, bool expected)
    {
        Assert.Equal(expected, GitChangesTreeBuilder.IsDirectoryPath(path));
    }

    // ---- Integration: real Workspace-as-Git-repository layout ------------------------------------

    [Fact]
    public async Task Workspace_repository_layout_renders_files_folders_and_child_repositories_correctly()
    {
        var root = Directory.CreateTempSubdirectory("graymoon-gcd-").FullName;
        try
        {
            await GitAsync(root, "init");
            await GitAsync(root, "config user.email test@example.com");
            await GitAsync(root, "config user.name Test");
            Directory.CreateDirectory(Path.Combine(root, ".claude"));
            await File.WriteAllTextAsync(Path.Combine(root, "CLAUDE.md"), "x\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".claude", "settings.json"), "{}\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "/GrayMoon/\n");
            await GitAsync(root, "add -A");
            await GitAsync(root, "commit -m init");

            // Ignored child repository (listed in .gitignore) and a child repository nobody ignored yet.
            foreach (var child in new[] { "GrayMoon", "GrayMoon.Release" })
            {
                var childPath = Path.Combine(root, child);
                Directory.CreateDirectory(childPath);
                await GitAsync(childPath, "init");
                await File.WriteAllTextAsync(Path.Combine(childPath, "README.md"), "child\n");
            }

            // Tracked root file and tracked nested file changed.
            await File.WriteAllTextAsync(Path.Combine(root, "CLAUDE.md"), "changed\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".claude", "settings.json"), "{ \"a\": 1 }\n");

            var output = await GitAsync(root, "status --porcelain=v2 -z --branch --untracked-files=all");
            var parsed = GitPorcelainV2Parser.Parse(output);
            var rows = Build(parsed.Changes.Select(c => new WorkspaceGitChangeEntryView
            {
                Path = c.Path,
                OriginalPath = c.OriginalPath,
                IndexChange = c.IndexChange,
                WorktreeChange = c.WorktreeChange,
                IsTracked = c.IsTracked,
                IsConflicted = c.IsConflicted,
                IsSubmodule = c.IsSubmodule,
            }).ToArray());

            // Tracked files appear as files, with the correct folder hierarchy.
            Assert.Contains(rows, r => r.Kind == GitChangesTreeRowKind.File && r.FilePath == "CLAUDE.md" && r.Depth == 2);
            var claudeFolder = Assert.Single(rows, r => r.Kind == GitChangesTreeRowKind.Folder && r.Label == ".claude");
            Assert.Contains(rows, r => r.Kind == GitChangesTreeRowKind.File && r.FilePath == ".claude/settings.json" && r.Depth == claudeFolder.Depth + 1);

            // The ignored child repository is absent; the non-ignored one is a directory, never a file.
            Assert.DoesNotContain(rows, r => r.Label == "GrayMoon");
            var release = Assert.Single(rows, r => r.Label == "GrayMoon.Release");
            Assert.Equal(GitChangesTreeRowKind.Folder, release.Kind);
            Assert.True(release.IsDirectoryEntry);
            Assert.DoesNotContain(rows, r => r.Kind == GitChangesTreeRowKind.File && (r.FilePath ?? "").StartsWith("GrayMoon", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
            catch
            {
                // best-effort
            }
        }
    }

    private static async Task<string> GitAsync(string workingDirectory, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed: {await p.StandardError.ReadToEndAsync()}");
        return stdout;
    }
}
