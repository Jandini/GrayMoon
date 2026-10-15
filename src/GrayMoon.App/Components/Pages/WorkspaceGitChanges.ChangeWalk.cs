using GrayMoon.App.Services.GitChanges;

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

    private bool CanWalkChanges =>
        !_isDiffLoading
        && !_changeWalkBusy
        && (_selectedRow is { Kind: GitChangesTreeRowKind.File } || _rows.Any(r => r.Kind == GitChangesTreeRowKind.File));

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
        var files = GetWalkableFiles();
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
    private List<GitChangesTreeRow> GetWalkableFiles()
    {
        if (_view == null)
        {
            return [];
        }

        var fileRows = GitChangesTreeBuilder.Build(_view, _filterQuery)
            .Where(r => r.Kind == GitChangesTreeRowKind.File)
            .ToList();

        return [.. FileOrdering.Order(fileRows)];
    }

    private async Task SelectWalkedFileAsync(GitChangesTreeRow row, DiffLanding landing)
    {
        EnsureAncestorsExpanded(row.FilePath!, row.WorkspaceRepositoryId, row.IsStagedSection);

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
