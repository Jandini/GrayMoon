namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// One page of search hits across every repository of a context. Hits are ordered by repository id, then by
/// path (ordinal), so <see cref="NextAfterKey"/> is a stable keyset cursor even while snapshots are replaced.
/// </summary>
public sealed record GitChangesSearchPage
{
    /// <summary>Sum of the repositories' snapshot versions; a change between pages means the cursor may be stale.</summary>
    public required long SnapshotVersion { get; init; }

    /// <summary>
    /// Total distinct matching paths across the whole context. Only computed on the first page (request without
    /// a cursor); null on later pages, where the caller keeps the first page's value.
    /// </summary>
    public int? TotalMatchCount { get; init; }

    public required IReadOnlyList<GitChangesSearchHit> Hits { get; init; }

    /// <summary>Pass as <c>afterKey</c> on the next request; null when every match has been returned.</summary>
    public string? NextAfterKey { get; init; }

    public bool HasMore => !string.IsNullOrEmpty(NextAfterKey);
}

/// <summary>One matching path with the repository it belongs to.</summary>
public sealed record GitChangesSearchHit
{
    public required int WorkspaceRepositoryId { get; init; }
    public required string RepositoryName { get; init; }
    public required WorkspaceGitChangeEntryView Entry { get; init; }
}
