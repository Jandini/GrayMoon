using System.Diagnostics;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    private GitChangesHydrationMode _hydrationMode = GitChangesHydrationMode.Full;
    private readonly GitChangesTreeNodeCache _treeNodeCache = new();

    /// <summary>
    /// Fetches in flight, keyed by (repo, section, prefix). The value is the cache object being filled, so a
    /// fetch started against a cache that was since invalidated (live update) is recognised as orphaned and a
    /// fresh fetch for the same key is never blocked by it.
    /// </summary>
    private readonly Dictionary<string, GitChangesTreeNodeCache.PrefixPageCache> _prefixFetchInFlight =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Folder keys already given a default collapsed state in Summary mode. First sight collapses them so
    /// expanding a repo does not treat every child folder as open (which left permanent spinners).
    /// </summary>
    private readonly HashSet<string> _summaryFoldersSeeded = new(StringComparer.Ordinal);

    /// <summary>Bumped by every projection load; an older load that finishes late must not overwrite a newer one.</summary>
    private int _loadGeneration;

    private CancellationTokenSource? _filterReloadCts;

    /// <summary>Delay before a filter edit that needs a hydration-mode switch triggers a reload.</summary>
    private const int FilterReloadDebounceMs = 300;

    /// <summary>
    /// Opens the context with either full hydration (small inventories) or summary-first + collapsed repos
    /// (large). Filter active in large mode falls back to full hydration until Phase 2 server search.
    /// </summary>
    private async Task LoadProjectionAsync()
    {
        var generation = Interlocked.Increment(ref _loadGeneration);

        if (_selectedContextId is not WorkspaceFeatureContextId ctxId)
        {
            var workspaceView = await ReadService.GetWorkspaceAsync(WorkspaceId, CancellationToken.None);
            if (generation != _loadGeneration)
                return;

            _view = workspaceView;
            _hydrationMode = GitChangesHydrationMode.Full;
            _treeNodeCache.Clear();
            _prefixFetchInFlight.Clear();
            _summaryFoldersSeeded.Clear();
            return;
        }

        var options = GitChangesOptions.Value;
        var filterActive = !string.IsNullOrWhiteSpace(_filterQuery);

        var summaries = await ReadService.GetContextSummariesAsync(WorkspaceId, ctxId, CancellationToken.None);
        if (generation != _loadGeneration)
            return;

        if (!filterActive && options.UseLargeHydration(summaries.TotalEntryCount))
        {
            _hydrationMode = GitChangesHydrationMode.Summary;
            _treeNodeCache.Clear();
            _prefixFetchInFlight.Clear();
            _summaryFoldersSeeded.Clear();
            InvalidateWalkableFilesCache();
            _view = GitChangesTreeBuilder.ViewFromSummaries(summaries);
            _collapsedKeys.Clear();
            GitChangesTreeBuilder.SeedCollapsedRepositories(_view, _collapsedKeys);

            // F5 / Ctrl+F5 / reload: put back whatever the user had expanded, then re-fetch those prefixes.
            await RestoreTreeStateAsync(ctxId.Value);
            if (generation != _loadGeneration)
                return;

            await HydrateRestoredTreeAsync();
            return;
        }

        var full = await ReadService.GetContextAsync(WorkspaceId, ctxId, CancellationToken.None);
        if (generation != _loadGeneration)
            return;

        _hydrationMode = GitChangesHydrationMode.Full;
        _treeNodeCache.Clear();
        _prefixFetchInFlight.Clear();
        _summaryFoldersSeeded.Clear();
        InvalidateWalkableFilesCache();
        _view = full;
        // Small / filtered path keeps today's default-expand: do not seed collapsed keys.
    }

    /// <summary>
    /// Replaces one repository in <see cref="_view"/> after a live snapshot (by <c>RepositoryId</c> from the hub).
    /// Never reloads sibling repositories' entries; in Summary mode it never reads this repository's entries
    /// either, only its status row, and re-fetches just the first page of each expanded prefix.
    /// </summary>
    private async Task PatchRepositoryLiveAsync(int workspaceId, int contextId, int repositoryId)
    {
        if (_selectedContextId is WorkspaceFeatureContextId selected && selected.Value != contextId)
            return;

        if (_view == null)
        {
            await LoadAsync(
                benchEventName: "live update handled",
                liveOutcome: "Reloaded",
                liveWorkspaceId: workspaceId,
                liveContextId: contextId);
            return;
        }

        var openStarted = Stopwatch.GetTimestamp();
        var heapBefore = GC.GetTotalMemory(forceFullCollection: false);
        long readMs = 0;
        long buildTreeMs = 0;
        var entryCount = 0;
        string outcome;

        try
        {
            if (_selectedContextId is not WorkspaceFeatureContextId ctx)
            {
                // Page has no resolved context yet - fall back to a full reload.
                await LoadAsync(
                    benchEventName: "live update handled",
                    liveOutcome: "Reloaded",
                    liveWorkspaceId: workspaceId,
                    liveContextId: contextId);
                return;
            }

            var summaryMode = _hydrationMode == GitChangesHydrationMode.Summary;
            var readStarted = Stopwatch.GetTimestamp();
            var patched = summaryMode
                ? await ReadService.GetRepositorySummaryAsync(workspaceId, ctx, repositoryId, CancellationToken.None)
                : await ReadService.GetRepositoryAsync(workspaceId, ctx, repositoryId, CancellationToken.None);
            readMs = GitChangesBench.ElapsedMs(readStarted);

            // The mode can change underneath us while we awaited a full reload.
            if (summaryMode != (_hydrationMode == GitChangesHydrationMode.Summary) || _view == null)
            {
                await LoadAsync(
                    benchEventName: "live update handled",
                    liveOutcome: "Reloaded",
                    liveWorkspaceId: workspaceId,
                    liveContextId: contextId);
                return;
            }

            if (patched is null)
            {
                var removed = _view.Repositories.FirstOrDefault(r => r.RepositoryId == repositoryId);
                if (removed is not null)
                    InvalidateRepositoryCaches(removed.WorkspaceRepositoryId);

                _view = _view with
                {
                    Repositories = _view.Repositories
                        .Where(r => r.RepositoryId != repositoryId)
                        .ToList(),
                };
            }
            else
            {
                ReplaceRepositoryInView(patched);
                if (summaryMode)
                    InvalidateRepositoryCaches(patched.WorkspaceRepositoryId);
            }

            if (ShouldSwitchHydrationMode())
            {
                // The inventory crossed FullHydrationMaxEntries: reopen so the right mode is used.
                await LoadAsync(
                    benchEventName: "live update handled",
                    liveOutcome: "Reloaded",
                    liveWorkspaceId: workspaceId,
                    liveContextId: contextId);
                return;
            }

            if (summaryMode && patched is not null)
                await RehydrateExpandedAsync(patched.WorkspaceRepositoryId);

            outcome = "PatchedOnly";
            entryCount = summaryMode
                ? (_view?.Repositories.Sum(r => r.StagedCount + r.ChangedCount) ?? 0)
                : (_view?.Repositories.Sum(r => r.Changes.Count) ?? 0);

            InvalidateWalkableFilesCache();
            ClearFilterIfNoChangesRemain();
            var buildStarted = Stopwatch.GetTimestamp();
            RebuildRows();
            buildTreeMs = GitChangesBench.ElapsedMs(buildStarted);
            await ClearSelectionIfStaleAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to patch Git Changes for repository {RepositoryId}", repositoryId);
            await LoadAsync(
                benchEventName: "live update handled",
                liveOutcome: "Reloaded",
                liveWorkspaceId: workspaceId,
                liveContextId: contextId);
            return;
        }

        _pendingBenchPaint = new PendingGitChangesBenchPaint
        {
            EventName = "live update handled",
            LoadStarted = openStarted,
            PaintStarted = Stopwatch.GetTimestamp(),
            ReadMs = readMs,
            BuildTreeMs = buildTreeMs,
            EntryCount = entryCount,
            HeapBeforeBytes = heapBefore,
            RestoreSelectionAfterPaint = false,
            LiveOutcome = outcome,
            LiveWorkspaceId = workspaceId,
            LiveContextId = contextId,
        };
        StateHasChanged();
    }

    /// <summary>
    /// True when the live-patched counts say the other hydration mode now applies. Full mode with an active
    /// filter never switches (the filter forces Full). Summary mode switches back only when even the
    /// double-counting Staged+Changed total fits the threshold, so the distinct path count surely does and the
    /// reload cannot flap straight back into Summary.
    /// </summary>
    private bool ShouldSwitchHydrationMode()
    {
        if (_view is null || !string.IsNullOrWhiteSpace(_filterQuery))
            return false;

        var options = GitChangesOptions.Value;
        return _hydrationMode == GitChangesHydrationMode.Full
            ? options.UseLargeHydration(_view.Repositories.Sum(r => r.Changes.Count))
            : !options.UseLargeHydration(_view.Repositories.Sum(r => r.StagedCount + r.ChangedCount));
    }

    private void InvalidateRepositoryCaches(int workspaceRepositoryId)
    {
        _treeNodeCache.InvalidateRepository(workspaceRepositoryId);
        var prefix = $"{workspaceRepositoryId}|";
        foreach (var key in _prefixFetchInFlight.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _prefixFetchInFlight.Remove(key);
    }

    private void ReplaceRepositoryInView(WorkspaceGitChangesRepositoryView patched)
    {
        if (_view is null)
            return;

        var list = _view.Repositories.ToList();
        var idx = list.FindIndex(r => r.RepositoryId == patched.RepositoryId
                                      || r.WorkspaceRepositoryId == patched.WorkspaceRepositoryId);
        if (idx >= 0)
            list[idx] = patched;
        else
            list.Add(patched);

        _view = _view with
        {
            Repositories = list.OrderBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private async Task ToggleExpandedAsync(GitChangesTreeRow row)
    {
        if (!row.HasChildren)
            return;

        var expanding = _collapsedKeys.Contains(row.Key);
        if (expanding)
            _collapsedKeys.Remove(row.Key);
        else
            _collapsedKeys.Add(row.Key);

        await RefreshTreeAsync();

        if (expanding && _hydrationMode == GitChangesHydrationMode.Summary)
            await EnsurePrefixHydratedAsync(row);

        await PersistTreeStateAsync();
    }

    private async Task EnsurePrefixHydratedAsync(GitChangesTreeRow row)
    {
        if (row.Kind is not (GitChangesTreeRowKind.Repository or GitChangesTreeRowKind.Folder))
            return;
        if (row.IsDirectoryEntry)
            return;

        var pathPrefix = row.Kind == GitChangesTreeRowKind.Repository
            ? string.Empty
            : GitChangesTreeBuilder.FolderRelativePathOf(row);

        // First page of this node, plus first pages of any descendant folders that are already open
        // (they lost their cache while an ancestor was collapsed or after a live update).
        await HydrateExpandedTreeAsync(row.WorkspaceRepositoryId, row.IsStagedSection, pathPrefix);
    }

    /// <summary>Raised by a "Loading more..." sentinel row: fetches the next page of its prefix.</summary>
    private Task LoadMoreAsync(GitChangesTreeRow row) =>
        row.Kind == GitChangesTreeRowKind.More
            ? FetchPrefixPageAsync(row.WorkspaceRepositoryId, row.IsStagedSection, row.PathPrefix ?? string.Empty)
            : Task.CompletedTask;

    private static string SectionKeyOf(bool isStagedSection) => isStagedSection ? "staged" : "changed";

    private static string NodeKeyOf(int workspaceRepositoryId, bool isStagedSection, string pathPrefix) =>
        string.IsNullOrEmpty(pathPrefix)
            ? $"{SectionKeyOf(isStagedSection)}/{workspaceRepositoryId}"
            : $"{SectionKeyOf(isStagedSection)}/{workspaceRepositoryId}/{pathPrefix}";

    /// <summary>
    /// Ensures the first page of <paramref name="pathPrefix"/> is cached, then does the same for every child
    /// folder the user has open. Never fetches closed folders and never fetches beyond a first page.
    /// </summary>
    private async Task HydrateExpandedTreeAsync(int workspaceRepositoryId, bool isStagedSection, string pathPrefix)
    {
        var cache = _treeNodeCache.TryGet(workspaceRepositoryId, isStagedSection, pathPrefix);
        if (cache is null || !cache.HasLoaded)
            await FetchPrefixPageAsync(workspaceRepositoryId, isStagedSection, pathPrefix);

        cache = _treeNodeCache.TryGet(workspaceRepositoryId, isStagedSection, pathPrefix);
        if (cache is null)
            return;

        foreach (var child in cache.Children.ToList())
        {
            if (!child.IsFolder || child.IsDirectoryEntry)
                continue;
            if (_collapsedKeys.Contains(NodeKeyOf(workspaceRepositoryId, isStagedSection, child.Path)))
                continue;

            await HydrateExpandedTreeAsync(workspaceRepositoryId, isStagedSection, child.Path);
        }
    }

    /// <summary>Re-opens every section of one repository that is currently expanded after its caches were dropped.</summary>
    private async Task RehydrateExpandedAsync(int workspaceRepositoryId)
    {
        var repo = _view?.Repositories.FirstOrDefault(r => r.WorkspaceRepositoryId == workspaceRepositoryId);
        if (repo is null)
            return;

        foreach (var staged in new[] { true, false })
        {
            if ((staged ? repo.StagedCount : repo.ChangedCount) <= 0)
                continue;
            if (_collapsedKeys.Contains(SectionKeyOf(staged)) || _collapsedKeys.Contains(NodeKeyOf(workspaceRepositoryId, staged, string.Empty)))
                continue;

            await HydrateExpandedTreeAsync(workspaceRepositoryId, staged, string.Empty);
        }
    }

    /// <summary>
    /// Fetches the next page of direct children of one prefix into the node cache (the first page when the
    /// cache is empty). One page per call: further pages load when the "Loading more..." sentinel scrolls into
    /// view, so the circuit only holds what the user has scrolled to.
    /// </summary>
    private async Task FetchPrefixPageAsync(int workspaceRepositoryId, bool isStagedSection, string pathPrefix)
    {
        if (_selectedContextId is not WorkspaceFeatureContextId ctxId)
            return;

        var cache = _treeNodeCache.GetOrCreate(workspaceRepositoryId, isStagedSection, pathPrefix);
        if (cache.IsComplete)
            return;

        var flightKey = $"{workspaceRepositoryId}|{isStagedSection}|{pathPrefix}";
        if (_prefixFetchInFlight.TryGetValue(flightKey, out var owner) && ReferenceEquals(owner, cache))
            return;

        _prefixFetchInFlight[flightKey] = cache;
        cache.IsLoading = true;
        cache.LoadFailed = false;
        if (cache.Children.Count == 0)
            await RefreshTreeAsync();

        bool IsCurrent() =>
            ReferenceEquals(_treeNodeCache.TryGet(workspaceRepositoryId, isStagedSection, pathPrefix), cache)
            && _prefixFetchInFlight.TryGetValue(flightKey, out var o)
            && ReferenceEquals(o, cache);

        try
        {
            var take = Math.Max(1, GitChangesOptions.Value.TreePageSize);
            var page = await ReadService.GetEntryPageAsync(
                WorkspaceId,
                ctxId,
                workspaceRepositoryId,
                isStagedSection,
                pathPrefix,
                cache.HasLoaded ? cache.NextAfterChildKey : null,
                take,
                filter: null,
                CancellationToken.None);

            if (!IsCurrent())
                return; // Invalidated by a live update while in flight; its replacement fetch owns the result.

            if (cache.HasLoaded && page.SnapshotVersion != cache.SnapshotVersion)
            {
                // A newer snapshot landed between pages: restart this prefix from its first page.
                cache.Reset(0);
                page = await ReadService.GetEntryPageAsync(
                    WorkspaceId,
                    ctxId,
                    workspaceRepositoryId,
                    isStagedSection,
                    pathPrefix,
                    afterChildKey: null,
                    take,
                    filter: null,
                    CancellationToken.None);

                if (!IsCurrent())
                    return;
            }

            cache.ApplyPage(page);
            SeedCollapsedFolderChildren(workspaceRepositoryId, isStagedSection, pathPrefix, page);
        }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                cache.LoadFailed = true;
                Logger.LogWarning(ex,
                    "Git Changes prefix page fetch failed for repo {WorkspaceRepositoryId} prefix depth {PrefixDepth}",
                    workspaceRepositoryId, pathPrefix.Count(c => c == '/') + (pathPrefix.Length == 0 ? 0 : 1));
                ToastService.ShowError("Could not load folder contents. Collapse and expand the folder to retry.");
            }
        }
        finally
        {
            cache.IsLoading = false;
            if (_prefixFetchInFlight.TryGetValue(flightKey, out var current) && ReferenceEquals(current, cache))
                _prefixFetchInFlight.Remove(flightKey);

            EvictHiddenCaches();
            await RefreshTreeAsync();
        }
    }

    /// <summary>
    /// Fetches pages of one prefix until the child with <paramref name="childKey"/> is cached, the prefix is
    /// exhausted, or a fetch fails / makes no progress. Used to reveal a specific file that may sit beyond the first page.
    /// </summary>
    private async Task FetchUntilChildAsync(int workspaceRepositoryId, bool isStagedSection, string pathPrefix, string childKey)
    {
        while (true)
        {
            var cache = _treeNodeCache.TryGet(workspaceRepositoryId, isStagedSection, pathPrefix);
            if (cache is { HasLoaded: true })
            {
                if (cache.Children.Any(c => c.ChildKey == childKey) || cache.IsComplete || cache.LoadFailed)
                    return;
            }

            var before = cache?.Children.Count ?? -1;
            var loadedBefore = cache?.HasLoaded ?? false;
            await FetchPrefixPageAsync(workspaceRepositoryId, isStagedSection, pathPrefix);

            var after = _treeNodeCache.TryGet(workspaceRepositoryId, isStagedSection, pathPrefix);
            if (after is null || (after.Children.Count == before && after.HasLoaded == loadedBefore))
                return;
        }
    }

    /// <summary>
    /// First time we see a folder under a prefix, collapse it. Expanding a repo then only shows that
    /// level; nested folders stay closed until the user opens them (and only then do we fetch).
    /// </summary>
    private void SeedCollapsedFolderChildren(
        int workspaceRepositoryId,
        bool isStagedSection,
        string pathPrefix,
        GitChangesEntryPage page)
    {
        if (_hydrationMode != GitChangesHydrationMode.Summary)
            return;

        var parentKey = NodeKeyOf(workspaceRepositoryId, isStagedSection, pathPrefix);

        foreach (var child in page.Children)
        {
            if (!child.IsFolder || child.IsDirectoryEntry)
                continue;

            var folderKey = $"{parentKey}/{child.Name}";
            if (_summaryFoldersSeeded.Add(folderKey))
                _collapsedKeys.Add(folderKey);
        }
    }

    /// <summary>
    /// Keeps retained child rows near <c>CircuitVisibleRowBudget</c> by dropping caches of prefixes the user has
    /// collapsed (or whose ancestor is collapsed). Layer B is untouched; expanding again re-fetches the first page.
    /// Caches of expanded prefixes are never dropped, so the virtualized window is always intact.
    /// </summary>
    private void EvictHiddenCaches()
    {
        var options = GitChangesOptions.Value;
        var budget = Math.Max(options.CircuitVisibleRowBudget, options.TreePageSize * 2);
        if (_treeNodeCache.TotalCachedChildren <= budget)
            return;

        _treeNodeCache.Evict((key, cache) => !cache.IsLoading && !IsPrefixPathExpanded(key));
    }

    private bool IsPrefixPathExpanded(GitChangesTreeNodeCache.PrefixKey key)
    {
        if (_collapsedKeys.Contains(SectionKeyOf(key.IsStagedSection)))
            return false;

        var current = NodeKeyOf(key.WorkspaceRepositoryId, key.IsStagedSection, string.Empty);
        if (_collapsedKeys.Contains(current))
            return false;

        foreach (var segment in key.PathPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = $"{current}/{segment}";
            if (_collapsedKeys.Contains(current))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Rebuild and paint on the circuit sync context. After <c>await</c> (SQLite page fetch), a bare
    /// <see cref="ComponentBase.StateHasChanged"/> can leave the spinner stuck until the next click.
    /// </summary>
    private Task RefreshTreeAsync() => InvokeAsync(() =>
    {
        RebuildRows();
        StateHasChanged();
    });

    /// <summary>
    /// Debounced reload for filter edits that must switch hydration mode. Each keystroke cancels the previous
    /// timer, so typing triggers one reload after the user pauses instead of one per character.
    /// </summary>
    private void ScheduleFilterReload()
    {
        _filterReloadCts?.Cancel();
        _filterReloadCts?.Dispose();
        var cts = _filterReloadCts = new CancellationTokenSource();
        _ = ReloadAfterFilterDelayAsync(cts.Token);
    }

    private async Task ReloadAfterFilterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FilterReloadDebounceMs, cancellationToken);
            if (_disposed)
                return;

            await InvokeAsync(() => LoadAsync());
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (Exception ex) when (!_disposed)
        {
            Logger.LogWarning(ex, "Git Changes filter reload failed for workspace {WorkspaceId}", WorkspaceId);
        }
    }

    private void CancelFilterReload()
    {
        _filterReloadCts?.Cancel();
        _filterReloadCts?.Dispose();
        _filterReloadCts = null;
    }
}
