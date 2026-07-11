using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;
using GrayMoon.Common.Search;

namespace GrayMoon.App.Services;

/// <summary>
/// Filter matcher for the Git Changes tree, following the existing <c>*SearchMatcher</c> convention
/// (see <see cref="WorkspaceRepositoryNameSearchMatcher"/>). Supports plain text plus field-prefixed
/// tokens: <c>repo:</c>, <c>status:</c>, <c>staged:</c>, <c>ext:</c>.
/// </summary>
public static class WorkspaceGitChangeSearchMatcher
{
    public static bool IsValidQuery(string? query) => FilterSearchMatcher.IsValidQuery(query);

    public static bool Matches(string? repositoryName, WorkspaceGitChangeEntryView entry, string? query) =>
        FilterSearchMatcher.Matches(query, term => MatchTerm(repositoryName, entry, term));

    private static bool MatchTerm(string? repositoryName, WorkspaceGitChangeEntryView entry, FilterSearchTerm term)
    {
        if (string.IsNullOrEmpty(term.Field))
        {
            return DefaultHaystack(repositoryName, entry).Contains(term.Value, StringComparison.OrdinalIgnoreCase);
        }

        return term.Field.ToLowerInvariant() switch
        {
            "repo" => (repositoryName ?? string.Empty).Contains(term.Value, StringComparison.OrdinalIgnoreCase),
            "status" => MatchesStatus(entry, term.Value),
            "staged" => MatchesBool(entry.IsStaged, term.Value),
            "ext" => MatchesExtension(entry.Path, term.Value),
            _ => DefaultHaystack(repositoryName, entry).Contains(term.Value, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string DefaultHaystack(string? repositoryName, WorkspaceGitChangeEntryView entry) =>
        $"{repositoryName} {entry.Path} {entry.OriginalPath}";

    private static bool MatchesStatus(WorkspaceGitChangeEntryView entry, string value)
    {
        var kind = value.ToLowerInvariant() switch
        {
            "modified" => GitChangeKind.Modified,
            "added" => GitChangeKind.Added,
            "deleted" => GitChangeKind.Deleted,
            "renamed" => GitChangeKind.Renamed,
            "copied" => GitChangeKind.Copied,
            "untracked" => GitChangeKind.Untracked,
            "conflict" or "unmerged" => GitChangeKind.Unmerged,
            "typechanged" => GitChangeKind.TypeChanged,
            _ => (GitChangeKind?)null,
        };

        return kind != null && (entry.IndexChange == kind || entry.WorktreeChange == kind);
    }

    private static bool MatchesBool(bool actual, string value) =>
        bool.TryParse(value, out var expected) && actual == expected;

    private static bool MatchesExtension(string path, string value)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        return string.Equals(extension, value.TrimStart('.'), StringComparison.OrdinalIgnoreCase);
    }
}
