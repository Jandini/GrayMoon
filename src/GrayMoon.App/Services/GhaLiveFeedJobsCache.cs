using System.Collections.Concurrent;
using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

/// <summary>
/// Coalesces concurrent GitHub Actions "list run jobs" polls for the same run across every consumer -
/// N <see cref="GhaWorkflowLiveTerminal"/> instances on different pages, the Actions grid's auto-poll, and
/// <see cref="WorkspacePushService"/>'s push-wait discovery can all be watching the same run id at once.
/// Whichever poller asks first within <see cref="CoalesceWindow"/> makes the real GitHub call; everyone else
/// asking for the same run id in that window gets the same result with zero additional requests. Singleton
/// because <see cref="GhaWorkflowLiveFeedService"/> is Scoped (one instance per Blazor circuit / DI scope) and
/// this needs to be shared across circuits and background scopes to actually dedupe anything.
/// </summary>
public interface IGhaLiveFeedJobsCache
{
    /// <summary>
    /// Returns true (with the cached response, which may itself be null for "no jobs yet") when a fresh-enough
    /// entry exists, so callers can distinguish "not cached" from "cached as empty/null" and avoid re-fetching
    /// a run that genuinely has zero jobs yet.
    /// </summary>
    bool TryGet(string runKey, out GitHubWorkflowJobsResponse? response);

    void Set(string runKey, GitHubWorkflowJobsResponse? response);
}

public sealed class GhaLiveFeedJobsCache : IGhaLiveFeedJobsCache
{
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(2);
    private const int MaxEntries = 500;

    private readonly ConcurrentDictionary<string, (GitHubWorkflowJobsResponse? Response, DateTimeOffset FetchedAt)> _entries = new();

    public bool TryGet(string runKey, out GitHubWorkflowJobsResponse? response)
    {
        response = null;
        if (string.IsNullOrWhiteSpace(runKey))
            return false;

        if (_entries.TryGetValue(runKey, out var entry) && DateTimeOffset.UtcNow - entry.FetchedAt < CoalesceWindow)
        {
            response = entry.Response;
            return true;
        }

        return false;
    }

    public void Set(string runKey, GitHubWorkflowJobsResponse? response)
    {
        if (string.IsNullOrWhiteSpace(runKey))
            return;

        // Crude bound instead of a real LRU/TTL sweep - fine since a fresh cache just means the next poll
        // for each key makes a normal call, and this only fires on very long-uptime sessions with many runs.
        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(runKey))
            _entries.Clear();

        _entries[runKey] = (response, DateTimeOffset.UtcNow);
    }
}
