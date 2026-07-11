using GrayMoon.App.Components.GitChanges;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    private GitDiffViewer? _diffViewerRef;
    private GitDiffDocument? _selectedDiff;
    private bool _isDiffLoading;
    private string? _diffError;

    // Normal/NewFile/DeletedFile all have valid Original/Modified content (one side may simply be
    // empty) and render in Monaco. Binary/TooLarge/UnsupportedEncoding/Error never send content and
    // must show a placeholder instead of attempting to diff nothing.
    private static bool RendersInMonaco(GitDiffContentState state) =>
        state is GitDiffContentState.Normal or GitDiffContentState.NewFile or GitDiffContentState.DeletedFile;

    private async Task LoadDiffAsync(GitChangesTreeRow row)
    {
        _selectedDiff = null;
        _diffError = null;
        _isDiffLoading = true;
        StateHasChanged();

        try
        {
            if (_diffViewerRef != null)
            {
                await _diffViewerRef.ClearAsync();
            }

            var link = await DbContext.WorkspaceRepositories
                .Include(l => l.Workspace)
                .Include(l => l.Repository)
                .FirstOrDefaultAsync(l => l.WorkspaceRepositoryId == row.WorkspaceRepositoryId);

            if (link?.Workspace == null || link.Repository == null)
            {
                _diffError = "Repository not found.";
                return;
            }

            var root = await WorkspaceService.GetRootPathForWorkspaceAsync(link.Workspace);
            if (string.IsNullOrWhiteSpace(root))
            {
                _diffError = "Workspace root path is not configured.";
                return;
            }

            var comparison = row.IsStagedSection ? GitDiffComparison.Staged : GitDiffComparison.Unstaged;
            var result = await AgentClient.GetDiffAsync(
                root, link.Workspace.Name, link.Repository.RepositoryName, row.FilePath!, comparison, CancellationToken.None);

            if (!result.Success || result.Diff == null)
            {
                _diffError = result.ErrorMessage ?? "Failed to load diff.";
                return;
            }

            _selectedDiff = result.Diff;

            if (RendersInMonaco(_selectedDiff.State) && _diffViewerRef != null)
            {
                await _diffViewerRef.SetDiffAsync(_selectedDiff);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load diff for {Path}", row.FilePath);
            _diffError = "Failed to load diff.";
        }
        finally
        {
            _isDiffLoading = false;
            StateHasChanged();
        }
    }
}
