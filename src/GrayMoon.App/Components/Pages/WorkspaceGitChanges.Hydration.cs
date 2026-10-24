using System.Diagnostics;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    private GitChangesHydrationMode _hydrationMode = GitChangesHydrationMode.Full;
    private readonly GitChangesTreeNodeCache _treeNodeCache = new();
    private readonly HashSet<string> _prefixFetchInFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens the context with either full hydration (small inventories) or summary-first + collapsed repos
    /// (large). Filter active in large mode falls back to full hydration until Phase 2 server search.
    /// </summary>
    private async Task LoadProjectionAsync()
    {
        if (_selectedContextId is not WorkspaceFeatureContextId ctxId)
        {
            _view = await ReadService.GetWorkspaceAsync(WorkspaceId, CancellationToken.None);
            _hydrationMode = GitChangesHydrationMode.Full;
            _treeNodeCache.Clear();
            return;
        }

        var options = GitChangesOptions.Value;
        var filterActive = !string.IsNullOrWhiteSpace(_filterQuery);

        var summaries = await ReadService.GetContextSummariesAsync(WorkspaceId, ctxId, CancellationToken.None);
        if (!filterActive && options.UseLargeHydration(summaries.TotalEntryCount))
        {
            _hydrationMode = GitChangesHydrationMode.Summary;
            _treeNodeCache.Clear();
            InvalidateWalkableFilesCache();
            _view = GitChangesTreeBuilder.ViewFromSummaries(summaries);
            _collapsedKeys.Clear();
            GitChangesTreeBuilder.SeedCollapsedRepositories(_view, _collapsedKeys);
            return;
        }

        _hydrationMode = GitChangesHydrationMode.Full;
        _treeNodeCache.Clear();
        InvalidateWalkableFilesCache();
        _view = await ReadService.GetContextAsync(WorkspaceId, ctxId, CancellationToken.None);
        // Small / filtered path keeps today's default-expand: do not seed collapsed keys.
    }

    /// <summary>
    /// Replaces one repository in <see cref="_view"/> after a live snapshot (by <c>RepositoryId</c> from the hub).
    /// Never reloads sibling repositories' entries.
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

            var readStarted = Stopwatch.GetTimestamp();
            var patched = await ReadService.GetRepositoryAsync(workspaceId, ctx, repositoryId, CancellationToken.None);
            readMs = GitChangesBench.ElapsedMs(readStarted);

            if (patched is null)
            {
                var removed = _view.Repositories.FirstOrDefault(r => r.RepositoryId == repositoryId);
                if (removed is not null)
                    _treeNodeCache.InvalidateRepository(removed.WorkspaceRepositoryId);

                _view = _view with
                {
                    Repositories = _view.Repositories
                        .Where(r => r.RepositoryId != repositoryId)
                        .ToList(),
                };
                outcome = "PatchedOnly";
            }
            else if (_hydrationMode == GitChangesHydrationMode.Summary)
            {
                var withoutEntries = patched with { Changes = [] };
                ReplaceRepositoryInView(withoutEntries);
                _treeNodeCache.InvalidateRepository(patched.WorkspaceRepositoryId);
                outcome = "PatchedOnly";

                // Re-fetch pages for prefixes that are currently expanded under this repo.
                await RefetchExpandedPrefixesAsync(patched.WorkspaceRepositoryId);
            }
            else
            {
                ReplaceRepositoryInView(patched);
                outcome = "PatchedOnly";
            }

            entryCount = _hydrationMode == GitChangesHydrationMode.Summary
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
            outcome = "Reloaded";
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

        // Fetch every page under this prefix so Expand makes every direct child scrollable (Virtualize
        // keeps DOM bounded). Nested folders still load on their own expand.
        await FetchPrefixPagesAsync(row.WorkspaceRepositoryId, row.IsStagedSection, pathPrefix, fetchAll: true);
    }

    /// <summary>
    /// Fetches one page (or all pages when <paramref name="fetchAll"/>) of direct children into the node cache.
    /// </summary>
    private async Task FetchPrefixPagesAsync(
        int workspaceRepositoryId,
        bool isStagedSection,
        string pathPrefix,
        bool fetchAll)
    {
        if (_selectedContextId is not WorkspaceFeatureContextId ctxId)
            return;

        var cache = _treeNodeCache.GetOrCreate(workspaceRepositoryId, isStagedSection, pathPrefix);
        // Already fully hydrated - a second expand/hydrate must not re-fetch page 0 (that duplicated
        // children and crashed Virtualize with duplicate @key values).
        if (cache.IsComplete && !cache.IsLoading)
            return;

        var flightKey = $"{workspaceRepositoryId}|{isStagedSection}|{pathPrefix}";
        if (!_prefixFetchInFlight.Add(flightKey))
            return;

        cache.IsLoading = true;
        await RefreshTreeAsync();

        try
        {
            var take = Math.Max(1, GitChangesOptions.Value.TreePageSize);
            // Resume only when we already have a partial page; otherwise start from the first page.
            string? after = cache.Children.Count > 0 ? cache.NextAfterChildKey : null;

            do
            {
                var page = await ReadService.GetEntryPageAsync(
                    WorkspaceId,
                    ctxId,
                    workspaceRepositoryId,
                    isStagedSection,
                    pathPrefix,
                    after,
                    take,
                    filter: null,
                    CancellationToken.None);

                if (cache.Children.Count > 0 && page.SnapshotVersion != cache.SnapshotVersion)
                {
                    cache.Reset(page.SnapshotVersion);
                    after = null;
                    continue;
                }

                if (cache.Children.Count == 0)
                    cache.Reset(page.SnapshotVersion);

                cache.ApplyPage(page);
                after = page.NextAfterChildKey;
                await RefreshTreeAsync();

                if (!fetchAll)
                    break;
            }
            while (!string.IsNullOrEmpty(after));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex,
                "Git Changes prefix page fetch failed for repo {WorkspaceRepositoryId} prefix {Prefix}",
                workspaceRepositoryId, pathPrefix);
        }
        finally
        {
            cache.IsLoading = false;
            _prefixFetchInFlight.Remove(flightKey);
            await RefreshTreeAsync();
        }
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

    private async Task RefetchExpandedPrefixesAsync(int workspaceRepositoryId)
    {
        var expanded = _rows
            .Where(r => r.WorkspaceRepositoryId == workspaceRepositoryId
                        && r.IsExpanded
                        && r.Kind is GitChangesTreeRowKind.Repository or GitChangesTreeRowKind.Folder
                        && !r.IsDirectoryEntry)
            .ToList();

        foreach (var row in expanded)
        {
            var prefix = row.Kind == GitChangesTreeRowKind.Repository
                ? string.Empty
                : GitChangesTreeBuilder.FolderRelativePathOf(row);
            var cache = _treeNodeCache.GetOrCreate(workspaceRepositoryId, row.IsStagedSection, prefix);
            cache.Reset(0);
            await FetchPrefixPagesAsync(workspaceRepositoryId, row.IsStagedSection, prefix, fetchAll: true);
        }
    }
}
