using System.Collections.Concurrent;

namespace GrayMoon.App.Services;

/// <summary>
/// Caches the last ETag + response body per (connector, request URI) so repeated polls of an unchanged
/// resource (workflow run jobs, branch runs) can send <c>If-None-Match</c> and get a free 304 instead of
/// re-downloading and re-billing the same body against the secondary/abuse rate limit every tick.
/// Singleton because <see cref="GitHubService"/> is a typed HttpClient (effectively transient) - the cache
/// must outlive any single request/poll to be useful across successive polls of the same run/branch.
/// </summary>
public interface IGitHubETagCache
{
    GitHubETagCacheEntry? TryGet(string key);

    void Set(string key, string etag, string body);
}

public readonly record struct GitHubETagCacheEntry(string ETag, string Body);

public sealed class GitHubETagCache : IGitHubETagCache
{
    // Bounds unbounded growth over a long-running app session (many distinct run ids/branches polled over
    // days) without needing a real TTL/LRU - simply drop everything once the cache gets unreasonably large.
    // A fresh cache just means the next poll for each key is a normal (non-conditional) GET.
    private const int MaxEntries = 1000;

    private readonly ConcurrentDictionary<string, GitHubETagCacheEntry> _entries = new();

    public GitHubETagCacheEntry? TryGet(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        return _entries.TryGetValue(key, out var entry) ? entry : null;
    }

    public void Set(string key, string etag, string body)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(etag))
            return;

        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
            _entries.Clear();

        _entries[key] = new GitHubETagCacheEntry(etag, body);
    }
}
