using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.GitChanges;

public enum GitChangesTreeRowKind
{
    Section,
    Repository,
    Folder,
    File,
}

/// <summary>One flattened, renderable row of the section-first Staged/Changed tree. The page renders this
/// list directly (no recursive component tree) - collapsed subtrees simply do not appear, satisfying the
/// "flatten only visible nodes" performance guidance for large change sets.</summary>
public sealed record GitChangesTreeRow
{
    /// <summary>Stable identity across rebuilds - used as the Blazor @key and for expand/collapse state.</summary>
    public required string Key { get; init; }

    public required GitChangesTreeRowKind Kind { get; init; }
    public required int Depth { get; init; }
    public required string Label { get; init; }

    /// <summary>Item count shown as a badge beside the label. Set only on Section rows.</summary>
    public int? Count { get; init; }

    /// <summary>True for the Staged section and everything under it; false for Changed.</summary>
    public bool IsStagedSection { get; init; }

    public int WorkspaceRepositoryId { get; init; }
    public string? RepositoryName { get; init; }

    /// <summary>Current branch name. Set only on Repository rows.</summary>
    public string? BranchName { get; init; }

    /// <summary>Repository-relative path. Set only on File rows.</summary>
    public string? FilePath { get; init; }
    public string? OriginalPath { get; init; }

    public GitChangeKind IndexChange { get; init; }
    public GitChangeKind WorktreeChange { get; init; }
    public bool IsConflicted { get; init; }

    public bool HasChildren { get; init; }
    public bool IsExpanded { get; init; } = true;

    /// <summary>
    /// True for a Folder row that stands for one Git change entry naming a whole directory (a path ending in "/"), which
    /// is how <c>git status --untracked-files=all</c> reports an untracked folder it does not descend into - in practice a
    /// nested Git repository that the repository does not ignore. Such a row is never a file: no diff, no file actions,
    /// nothing to expand. <see cref="FilePath"/> holds the entry's path (with the trailing "/").
    /// </summary>
    public bool IsDirectoryEntry { get; init; }
}

/// <summary>
/// Builds the section-first (Staged -&gt; Repository -&gt; Folder -&gt; File, Changed -&gt; Repository -&gt; Folder -&gt; File)
/// tree from a workspace's persisted Git Changes view. Pure function - filtering happens at the entry
/// level first, so a folder/repository row is only ever reconstructed for entries that survive the
/// filter, which naturally preserves matching ancestors without extra bookkeeping.
/// </summary>
public static class GitChangesTreeBuilder
{
    /// <summary>Recovers a Folder row's repo-relative path from its Key, which encodes
    /// "&lt;section&gt;/&lt;workspaceRepositoryId&gt;/&lt;segment&gt;/&lt;segment&gt;/..." - stripping the
    /// section/repo prefix leaves the folder's own repo-relative path.</summary>
    public static string FolderRelativePathOf(GitChangesTreeRow row) =>
        string.Join('/', row.Key.Split('/').Skip(2));

    /// <summary>
    /// True when a change entry names a directory rather than a file. Git marks those with a trailing "/" (an untracked
    /// folder it does not list file by file, such as a nested repository). Node kind is decided from this, never from
    /// whether a node happens to have children.
    /// </summary>
    public static bool IsDirectoryPath(string path) => path.EndsWith('/');

    public static IReadOnlyList<GitChangesTreeRow> Build(
        WorkspaceGitChangesView view,
        string? filterQuery,
        IReadOnlySet<string>? collapsedKeys = null)
    {
        collapsedKeys ??= new HashSet<string>();
        var rows = new List<GitChangesTreeRow>();

        var stagedRepos = view.Repositories
            .Select(r => (Repo: r, Entries: r.Changes.Where(c => c.IsStaged).ToList()))
            .Where(x => x.Entries.Count > 0)
            .ToList();

        var changedRepos = view.Repositories
            .Select(r => (Repo: r, Entries: r.Changes.Where(c => c.IsChanged).ToList()))
            .Where(x => x.Entries.Count > 0)
            .ToList();

        AppendSection(rows, "staged", "Staged", isStagedSection: true, stagedRepos, filterQuery, collapsedKeys);
        AppendSection(rows, "changed", "Changed", isStagedSection: false, changedRepos, filterQuery, collapsedKeys);

        return rows;
    }

    private static void AppendSection(
        List<GitChangesTreeRow> rows,
        string sectionKey,
        string sectionLabel,
        bool isStagedSection,
        List<(WorkspaceGitChangesRepositoryView Repo, List<WorkspaceGitChangeEntryView> Entries)> repos,
        string? filterQuery,
        IReadOnlySet<string> collapsedKeys)
    {
        var filteredRepos = repos
            .Select(x => (x.Repo, Entries: x.Entries
                .Where(e => WorkspaceGitChangeSearchMatcher.Matches(x.Repo.RepositoryName, e, filterQuery))
                .ToList()))
            .Where(x => x.Entries.Count > 0)
            .ToList();

        if (filteredRepos.Count == 0)
        {
            return;
        }

        var sectionExpanded = !collapsedKeys.Contains(sectionKey);
        var totalCount = filteredRepos.Sum(x => x.Entries.Count);

        rows.Add(new GitChangesTreeRow
        {
            Key = sectionKey,
            Kind = GitChangesTreeRowKind.Section,
            Depth = 0,
            Label = sectionLabel,
            Count = totalCount,
            IsStagedSection = isStagedSection,
            HasChildren = true,
            IsExpanded = sectionExpanded,
        });

        if (!sectionExpanded)
        {
            return;
        }

        foreach (var (repo, entries) in filteredRepos.OrderBy(x => x.Repo.RepositoryName, StringComparer.OrdinalIgnoreCase))
        {
            var repoKey = $"{sectionKey}/{repo.WorkspaceRepositoryId}";
            var repoExpanded = !collapsedKeys.Contains(repoKey);

            rows.Add(new GitChangesTreeRow
            {
                Key = repoKey,
                Kind = GitChangesTreeRowKind.Repository,
                Depth = 1,
                Label = repo.RepositoryName,
                IsStagedSection = isStagedSection,
                WorkspaceRepositoryId = repo.WorkspaceRepositoryId,
                RepositoryName = repo.RepositoryName,
                BranchName = repo.BranchName,
                HasChildren = true,
                IsExpanded = repoExpanded,
            });

            if (!repoExpanded)
            {
                continue;
            }

            var items = entries
                .Select(e => (Entry: e, Remaining: e.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)))
                .ToList();

            AppendLevel(rows, repoKey, 2, isStagedSection, repo, items, collapsedKeys);
        }
    }

    private static void AppendLevel(
        List<GitChangesTreeRow> rows,
        string parentKey,
        int depth,
        bool isStagedSection,
        WorkspaceGitChangesRepositoryView repo,
        List<(WorkspaceGitChangeEntryView Entry, string[] Remaining)> items,
        IReadOnlySet<string> collapsedKeys)
    {
        var folders = items
            .Where(i => i.Remaining.Length > 1)
            .GroupBy(i => i.Remaining[0])
            .ToList();
        var folderNames = folders.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        // A directory entry ("Child/") becomes a Directory-style Folder row. If the same folder already exists as a parent
        // of other entries, that folder row wins and the directory entry adds nothing; it is never turned into a file.
        var directoryEntries = items
            .Where(i => i.Remaining.Length == 1 && IsDirectoryPath(i.Entry.Path) && !folderNames.Contains(i.Remaining[0]))
            .GroupBy(i => i.Remaining[0])
            .Select(g => g.First())
            .ToList();

        var files = items
            .Where(i => i.Remaining.Length == 1 && !IsDirectoryPath(i.Entry.Path))
            .Select(i => i.Entry)
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase);

        var folderNodes = folders
            .Select(g => (Name: g.Key, Group: (IGrouping<string, (WorkspaceGitChangeEntryView Entry, string[] Remaining)>?)g, DirectoryEntry: (WorkspaceGitChangeEntryView?)null))
            .Concat(directoryEntries.Select(d => (Name: d.Remaining[0], Group: (IGrouping<string, (WorkspaceGitChangeEntryView Entry, string[] Remaining)>?)null, DirectoryEntry: (WorkspaceGitChangeEntryView?)d.Entry)))
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, group, directoryEntry) in folderNodes)
        {
            if (directoryEntry is not null)
            {
                rows.Add(new GitChangesTreeRow
                {
                    Key = $"{parentKey}/{name}",
                    Kind = GitChangesTreeRowKind.Folder,
                    Depth = depth,
                    Label = name,
                    IsStagedSection = isStagedSection,
                    WorkspaceRepositoryId = repo.WorkspaceRepositoryId,
                    RepositoryName = repo.RepositoryName,
                    FilePath = directoryEntry.Path,
                    IndexChange = directoryEntry.IndexChange,
                    WorktreeChange = directoryEntry.WorktreeChange,
                    HasChildren = false,
                    IsExpanded = false,
                    IsDirectoryEntry = true,
                });
                continue;
            }

            var folderKey = $"{parentKey}/{name}";
            var expanded = !collapsedKeys.Contains(folderKey);

            rows.Add(new GitChangesTreeRow
            {
                Key = folderKey,
                Kind = GitChangesTreeRowKind.Folder,
                Depth = depth,
                Label = name,
                IsStagedSection = isStagedSection,
                WorkspaceRepositoryId = repo.WorkspaceRepositoryId,
                RepositoryName = repo.RepositoryName,
                HasChildren = true,
                IsExpanded = expanded,
            });

            if (!expanded)
            {
                continue;
            }

            var nested = group!.Select(i => (i.Entry, Remaining: i.Remaining.Skip(1).ToArray())).ToList();
            AppendLevel(rows, folderKey, depth + 1, isStagedSection, repo, nested, collapsedKeys);
        }

        foreach (var entry in files)
        {
            rows.Add(MakeFileRow(parentKey, depth, isStagedSection, repo, entry));
        }
    }

    private static GitChangesTreeRow MakeFileRow(
        string parentKey,
        int depth,
        bool isStagedSection,
        WorkspaceGitChangesRepositoryView repo,
        WorkspaceGitChangeEntryView entry)
    {
        var segments = entry.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments.Length > 0 ? segments[^1] : entry.Path;

        return new GitChangesTreeRow
        {
            Key = $"{parentKey}/{entry.Path}",
            Kind = GitChangesTreeRowKind.File,
            Depth = depth,
            Label = fileName,
            IsStagedSection = isStagedSection,
            WorkspaceRepositoryId = repo.WorkspaceRepositoryId,
            RepositoryName = repo.RepositoryName,
            FilePath = entry.Path,
            OriginalPath = entry.OriginalPath,
            IndexChange = entry.IndexChange,
            WorktreeChange = entry.WorktreeChange,
            IsConflicted = entry.IsConflicted,
            HasChildren = false,
        };
    }
}
