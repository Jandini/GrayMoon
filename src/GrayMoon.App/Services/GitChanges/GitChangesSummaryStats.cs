using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.GitChanges;

public sealed record GitChangesKindChip(string Letter, string StatusClass, int Count, string Label);

/// <summary>
/// Workspace-level kind and line totals for the Git Changes header. Kind chips are derived from
/// persisted unstaged (Changed) entries so they move when the user stages or unstages. Line
/// totals come from persisted unstaged snapshot fields.
/// </summary>
public sealed record GitChangesSummaryStats(
    IReadOnlyList<GitChangesKindChip> Kinds,
    int Insertions,
    int Deletions,
    bool HasLineStats)
{
    private static readonly GitChangeKind[] DisplayOrder =
    [
        GitChangeKind.Modified,
        GitChangeKind.Added,
        GitChangeKind.Deleted,
        GitChangeKind.Renamed,
        GitChangeKind.Copied,
        GitChangeKind.Unmerged,
        GitChangeKind.TypeChanged,
    ];

    public static GitChangesSummaryStats From(WorkspaceGitChangesView? view)
    {
        if (view == null)
        {
            return new GitChangesSummaryStats([], 0, 0, false);
        }

        var counts = new Dictionary<GitChangeKind, int>();
        foreach (var repo in view.Repositories)
        {
            foreach (var entry in repo.Changes)
            {
                var kind = Classify(entry);
                if (kind == GitChangeKind.None)
                {
                    continue;
                }

                counts[kind] = counts.GetValueOrDefault(kind) + 1;
            }
        }

        var kinds = new List<GitChangesKindChip>(DisplayOrder.Length);
        foreach (var kind in DisplayOrder)
        {
            if (!counts.TryGetValue(kind, out var count) || count <= 0)
            {
                continue;
            }

            kinds.Add(new GitChangesKindChip(LetterOf(kind), StatusClassOf(kind), count, LabelOf(kind)));
        }

        var computed = false;
        var insertions = 0;
        var deletions = 0;
        foreach (var repo in view.Repositories)
        {
            if (repo.Insertions.HasValue || repo.Deletions.HasValue)
            {
                computed = true;
            }

            insertions += repo.Insertions ?? 0;
            deletions += repo.Deletions ?? 0;
        }

        return new GitChangesSummaryStats(kinds, insertions, deletions, computed);
    }

    /// <summary>Counts the kind shown in the Changed tree for this entry.
    /// Untracked counts as Added so it appears on the A chip. Staged-only
    /// entries are omitted so staging a file moves it off the chips.</summary>
    internal static GitChangeKind Classify(WorkspaceGitChangeEntryView entry)
    {
        if (!entry.IsChanged)
        {
            return GitChangeKind.None;
        }

        if (entry.IsConflicted)
        {
            return GitChangeKind.Unmerged;
        }

        var kind = entry.WorktreeChange;
        return kind == GitChangeKind.Untracked ? GitChangeKind.Added : kind;
    }

    private static string LetterOf(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Added => "A",
        GitChangeKind.Modified => "M",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Copied => "C",
        GitChangeKind.TypeChanged => "T",
        GitChangeKind.Unmerged => "U",
        _ => "",
    };

    private static string StatusClassOf(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Added => "added",
        GitChangeKind.Deleted => "deleted",
        GitChangeKind.Renamed => "renamed",
        GitChangeKind.Unmerged => "conflict",
        _ => "modified",
    };

    private static string LabelOf(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Added => "Added",
        GitChangeKind.Modified => "Modified",
        GitChangeKind.Deleted => "Deleted",
        GitChangeKind.Renamed => "Renamed",
        GitChangeKind.Copied => "Copied",
        GitChangeKind.TypeChanged => "Type changed",
        GitChangeKind.Unmerged => "Conflict",
        _ => "Unchanged",
    };
}
