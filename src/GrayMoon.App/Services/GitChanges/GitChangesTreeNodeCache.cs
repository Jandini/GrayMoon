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

    /// <summary>Drops every cached prefix for one repository. In-flight fetches holding the old cache objects detect this by reference.</summary>
    public void InvalidateRepository(int workspaceRepositoryId)
    {
        var toRemove = _pages.Keys.Where(k => k.WorkspaceRepositoryId == workspaceRepositoryId).ToList();
        foreach (var key in toRemove)
            _pages.Remove(key);
    }

    public void Clear() => _pages.Clear();

    /// <summary>Number of child rows currently retained across every cached prefix.</summary>
    public int TotalCachedChildren => _pages.Values.Sum(p => p.Children.Count);

    public IReadOnlyCollection<PrefixKey> Keys => _pages.Keys;

    /// <summary>Drops cached prefixes for which <paramref name="shouldEvict"/> is true (soft circuit budget helper).</summary>
    public int Evict(Func<PrefixKey, PrefixPageCache, bool> shouldEvict)
    {
        var toRemove = _pages.Where(kv => shouldEvict(kv.Key, kv.Value)).Select(kv => kv.Key).ToList();
        foreach (var key in toRemove)
            _pages.Remove(key);
        return toRemove.Count;
    }

    public readonly record struct PrefixKey(int WorkspaceRepositoryId, bool IsStagedSection, string PathPrefix);

    public sealed class PrefixPageCache
    {
        private readonly HashSet<string> _childKeys = new(StringComparer.Ordinal);

        public long SnapshotVersion { get; set; }
        public int TotalDirectChildCount { get; set; }
        public List<GitChangesTreeChild> Children { get; } = [];
        public string? NextAfterChildKey { get; set; }
        public bool IsLoading { get; set; }

        /// <summary>True once at least one page was applied for this cache (even an empty one).</summary>
        public bool HasLoaded { get; private set; }

        /// <summary>The last fetch for this prefix failed; the UI stops spinning and the next expand retries.</summary>
        public bool LoadFailed { get; set; }

        /// <summary>True after a page apply with no further keyset cursor.</summary>
        public bool IsComplete => HasLoaded && string.IsNullOrEmpty(NextAfterChildKey);

        public bool HasMore => HasLoaded && !string.IsNullOrEmpty(NextAfterChildKey);

        public void Reset(long snapshotVersion)
        {
            SnapshotVersion = snapshotVersion;
            TotalDirectChildCount = 0;
            Children.Clear();
            _childKeys.Clear();
            NextAfterChildKey = null;
            HasLoaded = false;
            LoadFailed = false;
        }

        /// <summary>Applies a page. Returns false (and ignores it) when the page belongs to a different snapshot version.</summary>
        public bool ApplyPage(GitChangesEntryPage page)
        {
            if (Children.Count == 0 && !HasLoaded)
                SnapshotVersion = page.SnapshotVersion;
            else if (page.SnapshotVersion != SnapshotVersion)
                return false;

            TotalDirectChildCount = page.TotalDirectChildCount;
            foreach (var child in page.Children)
            {
                // O(1) guard against a repeated fetch appending duplicates (duplicate @key crashes Virtualize).
                if (_childKeys.Add(child.ChildKey))
                    Children.Add(child);
            }

            NextAfterChildKey = page.HasMore ? page.NextAfterChildKey : null;
            HasLoaded = true;
            LoadFailed = false;
            return true;
        }
    }
}
