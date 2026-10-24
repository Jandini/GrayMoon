namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Circuit-only cache of hydrated direct-child pages for large-mode tree expand.
/// Keys are (workspaceRepositoryId, isStagedSection, pathPrefix). Evicting never deletes Layer B.
/// </summary>
public sealed class GitChangesTreeNodeCache
{
    private readonly Dictionary<PrefixKey, PrefixPageCache> _pages = new();

    public PrefixPageCache? TryGet(int workspaceRepositoryId, bool isStagedSection, string pathPrefix) =>
        _pages.TryGetValue(new PrefixKey(workspaceRepositoryId, isStagedSection, pathPrefix), out var page)
            ? page
            : null;

    public PrefixPageCache GetOrCreate(int workspaceRepositoryId, bool isStagedSection, string pathPrefix)
    {
        var key = new PrefixKey(workspaceRepositoryId, isStagedSection, pathPrefix);
        if (!_pages.TryGetValue(key, out var page))
        {
            page = new PrefixPageCache();
            _pages[key] = page;
        }

        return page;
    }

    public void InvalidateRepository(int workspaceRepositoryId)
    {
        var toRemove = _pages.Keys.Where(k => k.WorkspaceRepositoryId == workspaceRepositoryId).ToList();
        foreach (var key in toRemove)
            _pages.Remove(key);
    }

    public void Clear() => _pages.Clear();

    /// <summary>Drops caches for collapsed prefixes outside the keep set (soft circuit budget helper).</summary>
    public void EvictExcept(IReadOnlySet<PrefixKey> keep)
    {
        var toRemove = _pages.Keys.Where(k => !keep.Contains(k)).ToList();
        foreach (var key in toRemove)
            _pages.Remove(key);
    }

    public readonly record struct PrefixKey(int WorkspaceRepositoryId, bool IsStagedSection, string PathPrefix);

    public sealed class PrefixPageCache
    {
        public long SnapshotVersion { get; set; }
        public int TotalDirectChildCount { get; set; }
        public List<GitChangesTreeChild> Children { get; } = [];
        public string? NextAfterChildKey { get; set; }
        public bool IsLoading { get; set; }
        /// <summary>True after at least one successful page apply with no further keyset cursor.</summary>
        public bool IsComplete =>
            SnapshotVersion != 0
            && string.IsNullOrEmpty(NextAfterChildKey)
            && Children.Count >= TotalDirectChildCount;
        public bool HasMore => !IsComplete && !string.IsNullOrEmpty(NextAfterChildKey);

        public void Reset(long snapshotVersion)
        {
            SnapshotVersion = snapshotVersion;
            TotalDirectChildCount = 0;
            Children.Clear();
            NextAfterChildKey = null;
            IsLoading = false;
        }

        public void ApplyPage(GitChangesEntryPage page)
        {
            if (Children.Count == 0)
                SnapshotVersion = page.SnapshotVersion;
            else if (page.SnapshotVersion != SnapshotVersion)
            {
                // Stale mid-flight page - caller should invalidate and retry; ignore append.
                return;
            }

            TotalDirectChildCount = page.TotalDirectChildCount;
            foreach (var child in page.Children)
            {
                // Guard against a repeated first-page fetch appending duplicates (duplicate @key crash).
                if (Children.Any(c => c.ChildKey == child.ChildKey))
                    continue;
                Children.Add(child);
            }

            NextAfterChildKey = page.HasMore ? page.NextAfterChildKey : null;
            if (!page.HasMore || Children.Count >= TotalDirectChildCount)
                NextAfterChildKey = null;
        }
    }
}
