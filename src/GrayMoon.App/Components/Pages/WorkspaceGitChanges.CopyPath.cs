using GrayMoon.App.Services.GitChanges;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    /// <summary>Icon click on a tree row: File rows copy their own full path, Folder/Repository/Section
    /// rows copy every contained file's full path as multiline text. Always computed from the full
    /// underlying <see cref="_view"/> - like the section-wide Stage/Unstage all actions - so a collapsed
    /// folder or an active text filter never changes what gets copied.</summary>
    private async Task CopyPathAsync(GitChangesTreeRow row)
    {
        if (_workspace == null)
        {
            return;
        }

        var root = await WorkspaceService.GetRootPathForWorkspaceAsync(_workspace);
        if (string.IsNullOrWhiteSpace(root))
        {
            ToastService.ShowError("Workspace root is not configured.");
            return;
        }

        if (row.Kind == GitChangesTreeRowKind.File)
        {
            var path = BuildAbsoluteFilePath(root, _workspace.Name, row.RepositoryName!, row.FilePath!);
            await CopyToClipboardAsync(path, "Path copied to the clipboard");
            return;
        }

        var entries = GetContainedFileEntries(row);
        if (entries.Count == 0)
        {
            ToastService.Show("No files to copy.");
            return;
        }

        var text = string.Join('\n', entries.Select(e => BuildAbsoluteFilePath(root, _workspace.Name, e.RepositoryName, e.Path)));
        await CopyToClipboardAsync(text, $"{entries.Count} path{(entries.Count == 1 ? string.Empty : "s")} copied to the clipboard");
    }

    private async Task CopyToClipboardAsync(string text, string successMessage)
    {
        try
        {
            await Js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            ToastService.Show(successMessage);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Clipboard copy failed for Git Changes path(s)");
            ToastService.ShowError("Could not copy to clipboard.");
        }
    }

    /// <summary>Gathers every File change contained under a Folder/Repository/Section row from the full
    /// underlying view (not the rendered/collapsed rows), so collapsed subtrees and the active text
    /// filter never affect what gets copied.</summary>
    private List<(string RepositoryName, string Path)> GetContainedFileEntries(GitChangesTreeRow row)
    {
        var result = new List<(string RepositoryName, string Path)>();
        if (_view == null)
        {
            return result;
        }

        switch (row.Kind)
        {
            case GitChangesTreeRowKind.Section:
                foreach (var repo in _view.Repositories.OrderBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase))
                {
                    AppendRepoEntries(result, repo, row.IsStagedSection);
                }

                break;

            case GitChangesTreeRowKind.Repository:
            {
                var repo = _view.Repositories.FirstOrDefault(r => r.WorkspaceRepositoryId == row.WorkspaceRepositoryId);
                if (repo != null)
                {
                    AppendRepoEntries(result, repo, row.IsStagedSection);
                }

                break;
            }

            case GitChangesTreeRowKind.Folder:
            {
                var repo = _view.Repositories.FirstOrDefault(r => r.WorkspaceRepositoryId == row.WorkspaceRepositoryId);
                if (repo == null)
                {
                    break;
                }

                var prefix = GitChangesTreeBuilder.FolderRelativePathOf(row) + "/";
                var entries = repo.Changes
                    .Where(c => row.IsStagedSection ? c.IsStaged : c.IsChanged)
                    .Where(c => c.Path.StartsWith(prefix, StringComparison.Ordinal))
                    .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase);

                foreach (var entry in entries)
                {
                    result.Add((repo.RepositoryName, entry.Path));
                }

                break;
            }
        }

        return result;
    }

    private static void AppendRepoEntries(List<(string RepositoryName, string Path)> result, WorkspaceGitChangesRepositoryView repo, bool isStagedSection)
    {
        var entries = repo.Changes
            .Where(c => isStagedSection ? c.IsStaged : c.IsChanged)
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            result.Add((repo.RepositoryName, entry.Path));
        }
    }

    /// <summary>Builds an absolute path with backslashes always, regardless of the App container's OS -
    /// GrayMoon workspaces only ever exist on Windows machines (the Agent's host), and the App itself
    /// never touches the local filesystem so <see cref="Path.Combine"/> (which would use the container's
    /// separator) must not be used here.</summary>
    private static string BuildAbsoluteFilePath(string root, string workspaceName, string repositoryName, string relativePath)
    {
        var normalizedRoot = root.TrimEnd('\\', '/');
        var normalizedRelative = relativePath.Replace('/', '\\');
        return $"{normalizedRoot}\\{workspaceName}\\{repositoryName}\\{normalizedRelative}";
    }
}
