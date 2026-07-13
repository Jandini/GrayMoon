using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Tests;

public class GitChangesTreeBuilderTests
{
    private static WorkspaceGitChangeEntryView Entry(string path, GitChangeKind index = GitChangeKind.None, GitChangeKind worktree = GitChangeKind.None) => new()
    {
        Path = path,
        IndexChange = index,
        WorktreeChange = worktree,
    };

    private static WorkspaceGitChangesRepositoryView Repo(int id, string name, params WorkspaceGitChangeEntryView[] changes) => new()
    {
        WorkspaceRepositoryId = id,
        RepositoryId = id,
        RepositoryName = name,
        Changes = changes,
    };

    [Fact]
    public void Empty_workspace_produces_no_rows()
    {
        var view = new WorkspaceGitChangesView { WorkspaceId = 1, Repositories = [] };

        var rows = GitChangesTreeBuilder.Build(view, null);

        Assert.Empty(rows);
    }

    [Fact]
    public void Staged_only_change_appears_only_under_the_staged_section()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories = [Repo(1, "repo-a", Entry("file.txt", index: GitChangeKind.Added))],
        };

        var rows = GitChangesTreeBuilder.Build(view, null);

        Assert.Contains(rows, r => r.Kind == GitChangesTreeRowKind.Section && r.IsStagedSection);
        Assert.DoesNotContain(rows, r => r.Kind == GitChangesTreeRowKind.Section && !r.IsStagedSection);
        var fileRow = Assert.Single(rows, r => r.Kind == GitChangesTreeRowKind.File);
        Assert.True(fileRow.IsStagedSection);
    }

    [Fact]
    public void Unstaged_only_change_appears_only_under_the_changed_section()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories = [Repo(1, "repo-a", Entry("file.txt", worktree: GitChangeKind.Modified))],
        };

        var rows = GitChangesTreeBuilder.Build(view, null);

        Assert.Contains(rows, r => r.Kind == GitChangesTreeRowKind.Section && !r.IsStagedSection);
        Assert.DoesNotContain(rows, r => r.Kind == GitChangesTreeRowKind.Section && r.IsStagedSection);
    }

    [Fact]
    public void File_with_both_staged_and_unstaged_changes_appears_independently_in_both_sections()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories = [Repo(1, "repo-a", Entry("file.txt", index: GitChangeKind.Modified, worktree: GitChangeKind.Modified))],
        };

        var rows = GitChangesTreeBuilder.Build(view, null);

        var fileRows = rows.Where(r => r.Kind == GitChangesTreeRowKind.File).ToList();
        Assert.Equal(2, fileRows.Count);
        Assert.Contains(fileRows, r => r.IsStagedSection);
        Assert.Contains(fileRows, r => !r.IsStagedSection);
    }

    [Fact]
    public void Repository_with_only_staged_changes_does_not_appear_under_changed()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories =
            [
                Repo(1, "staged-repo", Entry("a.txt", index: GitChangeKind.Added)),
                Repo(2, "changed-repo", Entry("b.txt", worktree: GitChangeKind.Modified)),
            ],
        };

        var rows = GitChangesTreeBuilder.Build(view, null);

        var changedSectionRepoNames = rows
            .SkipWhile(r => !(r.Kind == GitChangesTreeRowKind.Section && !r.IsStagedSection))
            .Where(r => r.Kind == GitChangesTreeRowKind.Repository)
            .Select(r => r.RepositoryName)
            .ToList();

        Assert.Contains("changed-repo", changedSectionRepoNames);
        Assert.DoesNotContain("staged-repo", changedSectionRepoNames);
    }

    [Fact]
    public void Nested_folders_produce_nested_folder_rows_in_path_order()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories = [Repo(1, "repo-a", Entry("src/Services/GitService.cs", worktree: GitChangeKind.Modified))],
        };

        var rows = GitChangesTreeBuilder.Build(view, null);

        var folderLabels = rows.Where(r => r.Kind == GitChangesTreeRowKind.Folder).Select(r => r.Label).ToList();
        Assert.Equal(["src", "Services"], folderLabels);

        var srcRow = rows.Single(r => r.Kind == GitChangesTreeRowKind.Folder && r.Label == "src");
        var servicesRow = rows.Single(r => r.Kind == GitChangesTreeRowKind.Folder && r.Label == "Services");
        var fileRow = rows.Single(r => r.Kind == GitChangesTreeRowKind.File);

        Assert.True(servicesRow.Depth > srcRow.Depth);
        Assert.True(fileRow.Depth > servicesRow.Depth);
    }

    [Fact]
    public void Files_at_repository_root_have_no_folder_row()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories = [Repo(1, "repo-a", Entry("README.md", worktree: GitChangeKind.Modified))],
        };

        var rows = GitChangesTreeBuilder.Build(view, null);

        Assert.DoesNotContain(rows, r => r.Kind == GitChangesTreeRowKind.Folder);
        Assert.Single(rows, r => r.Kind == GitChangesTreeRowKind.File);
    }

    [Fact]
    public void Filter_preserves_matching_ancestors_and_excludes_non_matching_siblings()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories =
            [
                Repo(1, "repo-a",
                    Entry("src/Target.cs", worktree: GitChangeKind.Modified),
                    Entry("src/Other.cs", worktree: GitChangeKind.Modified),
                    Entry("docs/readme.md", worktree: GitChangeKind.Modified)),
            ],
        };

        var rows = GitChangesTreeBuilder.Build(view, "Target");

        var fileLabels = rows.Where(r => r.Kind == GitChangesTreeRowKind.File).Select(r => r.Label).ToList();
        Assert.Equal(["Target.cs"], fileLabels);

        // The matching file's ancestor chain (repo + src folder) must still be present.
        Assert.Contains(rows, r => r.Kind == GitChangesTreeRowKind.Repository && r.RepositoryName == "repo-a");
        Assert.Contains(rows, r => r.Kind == GitChangesTreeRowKind.Folder && r.Label == "src");
        Assert.DoesNotContain(rows, r => r.Kind == GitChangesTreeRowKind.Folder && r.Label == "docs");
    }

    [Fact]
    public void Filter_matching_nothing_produces_no_rows()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories = [Repo(1, "repo-a", Entry("file.txt", worktree: GitChangeKind.Modified))],
        };

        var rows = GitChangesTreeBuilder.Build(view, "nonexistent-zz");

        Assert.Empty(rows);
    }

    [Fact]
    public void Repo_field_filter_scopes_to_matching_repository_only()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories =
            [
                Repo(1, "graymoon-api", Entry("a.txt", worktree: GitChangeKind.Modified)),
                Repo(2, "graymoon-web", Entry("b.txt", worktree: GitChangeKind.Modified)),
            ],
        };

        var rows = GitChangesTreeBuilder.Build(view, "repo:api");

        var repoNames = rows.Where(r => r.Kind == GitChangesTreeRowKind.Repository).Select(r => r.RepositoryName).ToList();
        Assert.Equal(["graymoon-api"], repoNames);
    }

    [Fact]
    public void Collapsed_section_hides_all_descendants()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories = [Repo(1, "repo-a", Entry("file.txt", worktree: GitChangeKind.Modified))],
        };

        var rows = GitChangesTreeBuilder.Build(view, null, collapsedKeys: new HashSet<string> { "changed" });

        var sectionRow = Assert.Single(rows, r => r.Kind == GitChangesTreeRowKind.Section);
        Assert.False(sectionRow.IsExpanded);
        Assert.DoesNotContain(rows, r => r.Kind != GitChangesTreeRowKind.Section);
    }

    [Fact]
    public void Collapsed_repository_hides_its_files_but_not_the_section()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories = [Repo(1, "repo-a", Entry("file.txt", worktree: GitChangeKind.Modified))],
        };

        var rows = GitChangesTreeBuilder.Build(view, null, collapsedKeys: new HashSet<string> { "changed/1" });

        Assert.Contains(rows, r => r.Kind == GitChangesTreeRowKind.Section);
        var repoRow = Assert.Single(rows, r => r.Kind == GitChangesTreeRowKind.Repository);
        Assert.False(repoRow.IsExpanded);
        Assert.DoesNotContain(rows, r => r.Kind == GitChangesTreeRowKind.File);
    }

    [Fact]
    public void Repositories_are_ordered_alphabetically_within_a_section()
    {
        var view = new WorkspaceGitChangesView
        {
            WorkspaceId = 1,
            Repositories =
            [
                Repo(1, "zebra", Entry("a.txt", worktree: GitChangeKind.Modified)),
                Repo(2, "alpha", Entry("b.txt", worktree: GitChangeKind.Modified)),
            ],
        };

        var rows = GitChangesTreeBuilder.Build(view, null);

        var repoNames = rows.Where(r => r.Kind == GitChangesTreeRowKind.Repository).Select(r => r.RepositoryName).ToList();
        Assert.Equal(["alpha", "zebra"], repoNames);
    }
}
