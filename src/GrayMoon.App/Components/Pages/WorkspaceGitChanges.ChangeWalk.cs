using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Components.Pages;

// Next / previous change buttons: step through the changes of the open file, then carry on into the
// adjacent file (in IGitChangesFileOrdering order) instead of circling inside a single file.
public sealed partial class WorkspaceGitChanges
{
    /// <summary>Which change to land on when a diff finishes loading.</summary>
    private enum DiffLanding
    {
        /// <summary>Normal file selection: Monaco reveals its first change, the markdown preview stays at the top.</summary>
        Default,
        First,
        Last,
    }

    private bool _changeWalkBusy;

    /// <summary>
    /// Summary-mode walk order cannot use <c>_view.Changes</c> (empty). Cached full-context file rows from
    /// SQLite, invalidated when the projection or filter changes.
    /// </summary>
    private List<GitChangesTreeRow>? _walkableFilesCache;

    private bool CanWalkChanges =>
        !_isDiffLoading
        && !_changeWalkBusy
        && (_selectedRow is { Kind: GitChangesTreeRowKind.File }
            || _rows.Any(r => r.Kind == GitChangesTreeRowKind.File)
            || (_hydrationMode == GitChangesHydrationMode.Summary
                && _view?.Repositories.Any(r => r.StagedCount > 0 || r.ChangedCount > 0) == true));

    private Task NavigateNextChangeAsync() => NavigateChangeAsync(forward: true);

    private Task NavigatePreviousChangeAsync() => NavigateChangeAsync(forward: false);

    private async Task NavigateChangeAsync(bool forward)
    {
        if (_disposed || !CanWalkChanges)
        {
            return;
        }

        _changeWalkBusy = true;
        try
        {
            if (CanNavigateDiffChanges && await StepWithinFileAsync(forward, wrap: false))
            {
                return;
            }

            await WalkToAdjacentFileAsync(forward);
        }
        finally
        {
            _changeWalkBusy = false;
            StateHasChanged();
        }
    }

    /// <summary>Steps to the next / previous change of the open file. False when it was already on the last / first one.</summary>
    private async Task<bool> StepWithinFileAsync(bool forward, bool wrap)
    {
        if (IsMarkdownFile && _mdSurface == MdSurface.Preview)
        {
            if (_markdownViewerRef == null)
            {
                return true;
            }

            return forward
                ? await _markdownViewerRef.GoToNextChangeAsync(wrap)
                : await _markdownViewerRef.GoToPreviousChangeAsync(wrap);
        }

        if (_diffViewerRef == null)
        {
            return true;
        }

        return forward
            ? await _diffViewerRef.GoToNextChangeAsync(wrap)
            : await _diffViewerRef.GoToPreviousChangeAsync(wrap);
    }

    private async Task WalkToAdjacentFileAsync(bool forward)
    {
        var files = await GetWalkableFilesAsync();
        if (files.Count == 0)
        {
            return;
        }

        var current = _selectedRow is { Kind: GitChangesTreeRowKind.File } selected
            ? files.FindIndex(f => f.Key == selected.Key)
            : -1;

        if (files.Count == 1 && current == 0)
        {
            // Nothing else to walk to - keep the old behaviour of cycling inside the only file.
            if (CanNavigateDiffChanges)
            {
                await StepWithinFileAsync(forward, wrap: true);
            }

            return;
        }

        // Wraps past either end of the whole list, mirroring how a single file used to wrap.
        var next = current switch
        {
            -1 => forward ? 0 : files.Count - 1,
            _ => (current + (forward ? 1 : -1) + files.Count) % files.Count,
        };

        await SelectWalkedFileAsync(files[next], forward ? DiffLanding.First : DiffLanding.Last);
    }

    /// <summary>All File rows in walk order, ignoring which folders are collapsed but honouring the filter.</summary>
    private async Task<List<GitChangesTreeRow>> GetWalkableFilesAsync()
    {
        if (_view == null)
        {
            return [];
        }

        if (_hydrationMode == GitChangesHydrationMode.Full)
        {
            var fileRows = GitChangesTreeBuilder.Build(_view, _filterQuery)
                .Where(r => r.Kind == GitChangesTreeRowKind.File)
                .ToList();
            return [.. FileOrdering.Order(fileRows)];
        }

        if (_walkableFilesCache != null)
        {
            return _walkableFilesCache;
        }

        if (_selectedContextId is not WorkspaceFeatureContextId ctxId)
        {
            return [];
        }

        // One SQLite read for walk order only - not retained as the circuit tree inventory.
        var full = await ReadService.GetContextAsync(WorkspaceId, ctxId, CancellationToken.None);
        var rows = GitChangesTreeBuilder.Build(full, _filterQuery)
            .Where(r => r.Kind == GitChangesTreeRowKind.File)
            .ToList();
        _walkableFilesCache = [.. FileOrdering.Order(rows)];
        return _walkableFilesCache;
    }

    private void InvalidateWalkableFilesCache() => _walkableFilesCache = null;

    private async Task SelectWalkedFileAsync(GitChangesTreeRow row, DiffLanding landing)
    {
        await EnsureAncestorsExpandedAsync(row.FilePath!, row.WorkspaceRepositoryId, row.IsStagedSection);

        // Prefer the row instance the tree is rendering so the selection highlight and scroll target match.
        var target = FindFileRow(row.WorkspaceRepositoryId, row.FilePath!, row.IsStagedSection) ?? row;

        _multiSelectedKeys.Clear();
        _multiSelectAnchorKey = target.Key;
        _selectedRow = target;
        SelectionMemory.Set(WorkspaceId, new WorkspaceGitChangesSelectionMemory.Selection(
            target.WorkspaceRepositoryId, target.FilePath!, target.IsStagedSection));
        _scrollSelectionIntoViewPending = true;

        await LoadDiffAsync(target, landing);
    }
}
