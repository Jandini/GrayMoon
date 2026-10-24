using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Components.Pages;

// Large-inventory filtering. In Summary mode the circuit holds no entries, so a filter is executed by
// IWorkspaceGitChangesReadService.SearchEntriesAsync and only the hits fetched so far live in the circuit
// (grown page by page as the "Loading more matches..." sentinel scrolls into view, until every match is
// listed). Small inventories (Full mode) keep the in-circuit matcher.
public sealed partial class WorkspaceGitChanges
{
    private const string SearchMoreKeyPrefix = ":search-more:";

    /// <summary>Hits fetched so far for the active filter; null when no search overlay is active.</summary>
    private sealed class SearchState(string filter)
    {
        public string Filter { get; } = filter;
        public List<GitChangesSearchHit> Hits { get; } = [];
        public string? NextAfterKey { get; set; }
        public int? TotalMatches { get; set; }
        public long SnapshotVersion { get; set; }
        public bool IsLoading { get; set; }
        public bool HasMore => !string.IsNullOrEmpty(NextAfterKey);
    }

    private SearchState? _search;

    /// <summary>True while a debounced search is fetching its first page (the previous rows stay visible meanwhile).</summary>
    private bool _searchInFlight;

    /// <summary>Expand/collapse state of the search result tree. Separate from the browse tree and never persisted.</summary>
    private readonly HashSet<string> _searchCollapsedKeys = new(StringComparer.Ordinal);

    private CancellationTokenSource? _searchCts;

    /// <summary>True while the tree shows server-side search results instead of the browse tree.</summary>
    private bool SearchActive => _hydrationMode == GitChangesHydrationMode.Summary && _search is not null;

    private string? SearchSummaryText
    {
        get
        {
            if (!SearchActive || _search!.TotalMatches is not int total)
                return null;

            return total == 1 ? "1 match" : $"{total:N0} matches";
        }
    }

    /// <summary>Debounced search for the current filter text; each keystroke cancels the previous timer and query.</summary>
    private void ScheduleSearch()
    {
        CancelSearch();
        var cts = _searchCts = new CancellationTokenSource();
        _ = RunDebouncedSearchAsync(cts.Token);
    }

    private async Task RunDebouncedSearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FilterReloadDebounceMs, cancellationToken);
            if (_disposed)
                return;

            await InvokeAsync(async () =>
            {
                if (cancellationToken.IsCancellationRequested || _disposed)
                    return;

                // The previous rows (browse tree or earlier results) stay on screen while the first page loads and
                // are swapped for the new results in one render, so the tree never blanks between searches.
                var state = new SearchState(_filterQuery) { IsLoading = true };
                _searchInFlight = true;
                StateHasChanged();

                try
                {
                    await FetchSearchPageAsync(state, cancellationToken);
                    if (cancellationToken.IsCancellationRequested || _disposed)
                        return;

                    _search = state;
                    _searchCollapsedKeys.Clear();
                }
                finally
                {
                    if (!cancellationToken.IsCancellationRequested)
                        _searchInFlight = false;
                }

                RebuildRows();
                StateHasChanged();
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (Exception ex) when (!_disposed)
        {
            Logger.LogWarning(ex, "Git Changes search failed for workspace {WorkspaceId}", WorkspaceId);
            ToastService.ShowError("Could not search the changes.");
        }
    }

    /// <summary>
    /// Applies a filter edit in Summary mode: blank returns to the browse tree, anything else schedules the
    /// debounced server-side search. Supersedes a pending hydration-mode reload.
    /// </summary>
    private void ApplySummaryFilter(string value)
    {
        CancelFilterReload();
        if (string.IsNullOrWhiteSpace(value))
        {
            ClearSearch();
            RebuildRows();
            return;
        }

        ScheduleSearch();
    }

    /// <summary>
    /// Every file row matching the current filter (all when blank), read page by page from Layer B. Used for
    /// change-walk order only; the caller holds the list weakly and the circuit never keeps it.
    /// </summary>
    private async Task<List<GitChangesTreeRow>> CollectWalkableFilesFromSearchAsync()
    {
        var state = new SearchState(_filterQuery);
        do
        {
            await FetchSearchPageAsync(state, CancellationToken.None);
        }
        while (state.HasMore);

        var byRepo = state.Hits
            .GroupBy(h => h.WorkspaceRepositoryId)
            .ToDictionary(g => g.Key, g => g.Select(h => h.Entry).ToList());
        var view = _view! with
        {
            Repositories = _view.Repositories
                .Where(r => byRepo.ContainsKey(r.WorkspaceRepositoryId))
                .Select(r => r with { Changes = byRepo[r.WorkspaceRepositoryId] })
                .ToList(),
        };

        return GitChangesTreeBuilder.Build(view, filterQuery: null)
            .Where(r => r.Kind == GitChangesTreeRowKind.File)
            .ToList();
    }

    /// <summary>Cancels any pending or in-flight search query (does not clear the results).</summary>
    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }

    /// <summary>Leaves search mode: the browse tree (and its page caches, untouched meanwhile) is shown again.</summary>
    private void ClearSearch()
    {
        CancelSearch();
        _searchInFlight = false;
        _search = null;
        _searchCollapsedKeys.Clear();
    }

    /// <summary>
    /// Fetches one page into <paramref name="state"/> (the first page when it holds no hits yet). If a newer
    /// snapshot landed between pages the cursor is stale, so the search restarts from its first page.
    /// </summary>
    private async Task FetchSearchPageAsync(SearchState state, CancellationToken cancellationToken)
    {
        if (_selectedContextId is not WorkspaceFeatureContextId ctxId)
        {
            state.IsLoading = false;
            return;
        }

        state.IsLoading = true;
        try
        {
            var take = Math.Max(1, GitChangesOptions.Value.TreePageSize);
            var after = state.Hits.Count == 0 ? null : state.NextAfterKey;
            var page = await ReadService.SearchEntriesAsync(
                WorkspaceId, ctxId, state.Filter, after, take, cancellationToken);

            if (after is not null && page.SnapshotVersion != state.SnapshotVersion)
            {
                state.Hits.Clear();
                state.NextAfterKey = null;
                state.TotalMatches = null;
                page = await ReadService.SearchEntriesAsync(
                    WorkspaceId, ctxId, state.Filter, afterKey: null, take, cancellationToken);
            }

            state.SnapshotVersion = page.SnapshotVersion;
            state.Hits.AddRange(page.Hits);
            state.NextAfterKey = page.NextAfterKey;
            state.TotalMatches ??= page.TotalMatchCount;
        }
        finally
        {
            state.IsLoading = false;
        }
    }

    /// <summary>Raised by the search "Loading more matches..." sentinel: fetches the next page of hits.</summary>
    private async Task LoadMoreSearchAsync()
    {
        var state = _search;
        if (state is null || state.IsLoading || !state.HasMore)
            return;

        var token = _searchCts?.Token ?? CancellationToken.None;
        try
        {
            await FetchSearchPageAsync(state, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Git Changes search page fetch failed for workspace {WorkspaceId}", WorkspaceId);
            state.NextAfterKey = null; // Stops the sentinel from retrying in a loop.
            ToastService.ShowError("Could not load more matches. Edit the filter to search again.");
        }

        if (ReferenceEquals(_search, state))
            await RefreshTreeAsync();
    }

    /// <summary>
    /// Runs the first search page synchronously with the projection load (open / reload), so the first paint
    /// already shows results. Clears the overlay when the filter is blank.
    /// </summary>
    private async Task LoadSearchForProjectionAsync()
    {
        if (string.IsNullOrWhiteSpace(_filterQuery))
        {
            ClearSearch();
            return;
        }

        CancelSearch();
        _searchCollapsedKeys.Clear();
        var state = new SearchState(_filterQuery);
        await FetchSearchPageAsync(state, CancellationToken.None);
        _search = state;
    }

    /// <summary>
    /// After a live snapshot the old cursor and hits are stale: re-run the search, fetching pages until the
    /// user's already-loaded depth is restored (or the matches run out), so their scroll position survives.
    /// </summary>
    private async Task RefreshSearchAsync()
    {
        var previous = _search;
        if (previous is null)
            return;

        var depth = previous.Hits.Count;
        var state = new SearchState(previous.Filter);
        await FetchSearchPageAsync(state, CancellationToken.None);
        while (state.HasMore && state.Hits.Count < depth)
            await FetchSearchPageAsync(state, CancellationToken.None);

        _search = state;
    }

    /// <summary>
    /// Search result rows: the loaded hits grouped by section, repository and folder (every folder open by
    /// default), plus a sentinel while more matches exist. Section counts are hidden until every page is
    /// loaded because they would otherwise count only the hits fetched so far.
    /// </summary>
    private List<GitChangesTreeRow> BuildSearchRows()
    {
        var state = _search!;
        var byRepo = state.Hits
            .GroupBy(h => h.WorkspaceRepositoryId)
            .ToDictionary(g => g.Key, g => g.Select(h => h.Entry).ToList());

        var view = _view! with
        {
            Repositories = _view.Repositories
                .Where(r => byRepo.ContainsKey(r.WorkspaceRepositoryId))
                .Select(r => r with { Changes = byRepo[r.WorkspaceRepositoryId] })
                .ToList(),
        };

        var rows = GitChangesTreeBuilder.Build(view, filterQuery: null, _searchCollapsedKeys);
        if (state.HasMore)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Kind == GitChangesTreeRowKind.Section)
                    rows[i] = rows[i] with { Count = null };
            }

            rows.Add(new GitChangesTreeRow
            {
                Key = $"{SearchMoreKeyPrefix}{state.Hits.Count}",
                Kind = GitChangesTreeRowKind.More,
                Depth = 1,
                Label = "Loading more matches...",
            });
        }

        return rows;
    }

    private static bool IsSearchMoreRow(GitChangesTreeRow row) =>
        row.Kind == GitChangesTreeRowKind.More
        && row.Key.StartsWith(SearchMoreKeyPrefix, StringComparison.Ordinal);

    private async Task ToggleSearchExpandedAsync(GitChangesTreeRow row)
    {
        if (!_searchCollapsedKeys.Remove(row.Key))
            _searchCollapsedKeys.Add(row.Key);

        await RefreshTreeAsync();
    }

    /// <summary>
    /// Reveals a file in the search results: opens its ancestors, then pages further hits until the file's
    /// row exists (or the matches are exhausted, e.g. the file does not match the filter).
    /// </summary>
    private async Task EnsureSearchHitLoadedAsync(string filePath, int workspaceRepositoryId, bool isStagedSection)
    {
        var section = isStagedSection ? "staged" : "changed";
        var current = $"{section}/{workspaceRepositoryId}";
        _searchCollapsedKeys.Remove(section);
        _searchCollapsedKeys.Remove(current);
        var segments = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = $"{current}/{segments[i]}";
            _searchCollapsedKeys.Remove(current);
        }

        RebuildRows();
        while (_search is { HasMore: true, IsLoading: false } state
               && FindFileRow(workspaceRepositoryId, filePath, isStagedSection) is null)
        {
            var before = state.Hits.Count;
            await LoadMoreSearchAsync();
            RebuildRows();
            if (_search is null || _search.Hits.Count == before)
                break;
        }

        await RefreshTreeAsync();
    }
}
