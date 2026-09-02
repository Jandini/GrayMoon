using GrayMoon.App.Models;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Components.Pages;

/// <summary>
/// "Undo" (discard) actions for unstaged changes only - File, Folder, Repository, multi-selected files,
/// and the whole Changed section. Every entry point shows a destructive-action confirmation modal
/// stating how many files will be discarded before doing any git work, then - unlike the lightweight
/// inline indicator used for single-file Stage/Unstage clicks - every discard runs behind the page's
/// <see cref="StartPageJob"/> LoadingOverlay/terminal with a "Reverting x of y..." progress message, since
/// discard is destructive and should always give the user visible feedback while it runs. Reuses
/// <see cref="PersistMutationResultAsync"/> so discard's resulting snapshot flows through the same
/// write-queue pipeline as every other Git Changes mutation and cannot race with them (only one page job
/// runs at a time).
/// </summary>
public sealed partial class WorkspaceGitChanges
{
    private const int ConfirmDetailItemCap = 10;
    private const string UndoFooterNote = "This cannot be undone.";

    private ConfirmModalState _confirmModal = new();

    private void CloseConfirmModal()
    {
        _confirmModal = _confirmModal with
        {
            IsVisible = false,
            ButtonText = "Yes",
            PendingAction = null,
            Details = Array.Empty<ConfirmDetailGroup>(),
            FooterNote = null,
            ConfirmIsDanger = false,
        };
        StateHasChanged();
    }

    private async Task OnConfirmModalYesAsync()
    {
        var action = _confirmModal.PendingAction;
        CloseConfirmModal();
        if (action != null)
        {
            await action();
        }
    }

    private void ShowConfirm(
        string message,
        Func<Task> onConfirm,
        string confirmButtonText = "Yes",
        IReadOnlyList<ConfirmDetailGroup>? details = null,
        string? footerNote = null,
        bool confirmIsDanger = false)
    {
        _confirmModal = _confirmModal with
        {
            IsVisible = true,
            Message = message,
            ButtonText = confirmButtonText,
            PendingAction = onConfirm,
            Details = details ?? Array.Empty<ConfirmDetailGroup>(),
            FooterNote = footerNote,
            ConfirmIsDanger = confirmIsDanger,
        };
        StateHasChanged();
    }

    /// <summary>Undo for a single File (<see cref="GitChangeOperationScope.ExplicitPaths"/>), Folder, or
    /// Repository row. Computes the affected file count from the persisted view before asking for
    /// confirmation - never from the rendered/filtered tree - so the count is accurate even under an
    /// active filter.</summary>
    private void ConfirmDiscardFileOrFolder(int workspaceRepositoryId, GitChangeOperationScope scope, IReadOnlyList<string> paths, string rowKey)
    {
        if (!AgentBridge.IsAgentConnected)
        {
            ToastService.ShowError("Agent not connected. Start GrayMoon.Agent and try again.");
            return;
        }

        if (IsMutating(workspaceRepositoryId))
        {
            return;
        }

        var repo = _view?.Repositories.FirstOrDefault(r => r.WorkspaceRepositoryId == workspaceRepositoryId);
        if (repo == null)
        {
            return;
        }

        var (fileCount, details) = scope switch
        {
            GitChangeOperationScope.Repository => BuildRepositoryUndoDetails(repo),
            GitChangeOperationScope.Folder => BuildFolderUndoDetails(repo, paths[0]),
            _ => BuildFileUndoDetails(repo, paths),
        };

        if (fileCount == 0)
        {
            return;
        }

        var message = $"Undo changes to {fileCount} file{(fileCount == 1 ? "" : "s")}?";

        ShowConfirm(message, () => scope == GitChangeOperationScope.Repository
            ? RunRepositoryScopedDiscardJobAsync(workspaceRepositoryId)
            : RunMutationAsync(workspaceRepositoryId, rowKey, isDiscard: true, async (root, wsName, repoName, repositoryId) =>
            {
                var result = await AgentClient.DiscardAsync(root, wsName, repoName, scope, paths, CancellationToken.None);
                await PersistMutationResultAsync(workspaceRepositoryId, repositoryId, result.Success, result.Snapshot, result.ErrorMessage);
            }), "Undo", details, UndoFooterNote, confirmIsDanger: true);
    }

    private static (int FileCount, IReadOnlyList<ConfirmDetailGroup> Details) BuildRepositoryUndoDetails(
        WorkspaceGitChangesRepositoryView repo)
    {
        var files = repo.Changes.Where(c => c.IsChanged).Select(c => c.Path).ToList();
        IReadOnlyList<ConfirmDetailGroup> details =
        [
            new("Repository", [new ConfirmDetailItem(repo.RepositoryName, FileCountLabel(files.Count))]),
            new("Files", CapDetailItems(files)),
        ];
        return (files.Count, details);
    }

    private static (int FileCount, IReadOnlyList<ConfirmDetailGroup> Details) BuildFolderUndoDetails(
        WorkspaceGitChangesRepositoryView repo,
        string folderPath)
    {
        var files = repo.Changes
            .Where(c => c.IsChanged && IsUnderFolder(c.Path, folderPath))
            .Select(c => c.Path)
            .ToList();
        var lastSegment = LastPathSegment(folderPath);
        var folderItem = lastSegment == folderPath
            ? new ConfirmDetailItem(folderPath)
            : new ConfirmDetailItem(lastSegment, folderPath);
        IReadOnlyList<ConfirmDetailGroup> details =
        [
            new("Repository", [new ConfirmDetailItem(repo.RepositoryName)]),
            new("Folder", [folderItem]),
            new("Files", CapDetailItems(files)),
        ];
        return (files.Count, details);
    }

    private static (int FileCount, IReadOnlyList<ConfirmDetailGroup> Details) BuildFileUndoDetails(
        WorkspaceGitChangesRepositoryView repo,
        IReadOnlyList<string> paths)
    {
        IReadOnlyList<ConfirmDetailGroup> details =
        [
            new("Repository", [new ConfirmDetailItem(repo.RepositoryName)]),
            new(paths.Count == 1 ? "File" : "Files", CapDetailItems(paths)),
        ];
        return (paths.Count, details);
    }

    private static bool IsUnderFolder(string path, string folderPath) =>
        path == folderPath || path.StartsWith(folderPath + "/", StringComparison.Ordinal);

    private static string LastPathSegment(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static string FileCountLabel(int count) =>
        $"{count} file{(count == 1 ? "" : "s")}";

    private static IReadOnlyList<ConfirmDetailItem> CapDetailItems(IReadOnlyList<string> texts)
    {
        if (texts.Count > ConfirmDetailItemCap)
        {
            return [new ConfirmDetailItem(FileCountLabel(texts.Count))];
        }

        return texts.Select(t => new ConfirmDetailItem(t)).ToList();
    }

    /// <summary>Repository-scope discard, mirroring <see cref="RunRepositoryScopedMutationJobAsync"/> -
    /// runs behind the page's LoadingOverlay/terminal job since it can touch every unstaged file/directory
    /// in the repository (via `git clean`/`git restore`).</summary>
    private Task RunRepositoryScopedDiscardJobAsync(int workspaceRepositoryId)
    {
        if (!AgentBridge.IsAgentConnected)
        {
            ToastService.ShowError("Agent not connected. Start GrayMoon.Agent and try again.");
            return Task.CompletedTask;
        }

        if (IsJobRunning)
        {
            return Task.CompletedTask;
        }

        StartPageJob("Undoing changes in repository...", async (job, ct) =>
        {
            var resolved = await ResolveRepositoryAsync(workspaceRepositoryId);
            if (resolved == null)
            {
                ToastService.ShowError("Repository not found or workspace root is not configured.");
                return;
            }

            var result = await AgentClient.DiscardAsync(
                resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName,
                GitChangeOperationScope.Repository, [], ct);

            // reload:false - StartPageJob's own ReloadOnSuccess does the final LoadAsync() once this job
            // body returns, same as the repository-scoped Stage/Unstage job.
            await PersistMutationResultAsync(workspaceRepositoryId, resolved.Value.RepositoryId, result.Success, result.Snapshot, result.ErrorMessage, reload: false);

            await Task.Delay(150, ct);
        });

        return Task.CompletedTask;
    }

    /// <summary>Undo for every unstaged change across every repository in the Changed section - the discard
    /// equivalent of <see cref="StageAllChangedAsync"/>. Same bounded fan-out idiom (shared
    /// <see cref="GitChangesOptions"/> concurrency limit, LoadingOverlay job) as <see cref="BulkSectionActionAsync"/>.</summary>
    private void ConfirmDiscardAllChanged()
    {
        if (!AgentBridge.IsAgentConnected)
        {
            ToastService.ShowError("Agent not connected. Start GrayMoon.Agent and try again.");
            return;
        }

        var targets = (_view?.Repositories ?? []).Where(r => r.ChangedCount > 0).ToList();
        if (targets.Count == 0 || IsJobRunning)
        {
            return;
        }

        var fileCount = targets.Sum(r => r.ChangedCount);
        var message = $"Undo all changes across {targets.Count} repositor{(targets.Count == 1 ? "y" : "ies")} ({fileCount} file{(fileCount == 1 ? "" : "s")})?";
        var details = new ConfirmDetailGroup[]
        {
            new("Repositories", targets
                .Select(r => new ConfirmDetailItem(r.RepositoryName, FileCountLabel(r.ChangedCount)))
                .ToList()),
        };

        ShowConfirm(message, () =>
        {
            RunDiscardAllChangedJob(targets);
            return Task.CompletedTask;
        }, "Undo", details, UndoFooterNote, confirmIsDanger: true);
    }

    private void RunDiscardAllChangedJob(List<WorkspaceGitChangesRepositoryView> targets)
    {
        if (IsJobRunning)
        {
            return;
        }

        var label = targets.Count == 1
            ? $"Undoing changes in {targets[0].RepositoryName}..."
            : $"Undoing changes in {targets.Count} repositories...";

        StartPageJob(label, async (job, ct) =>
        {
            var completed = 0;
            using var semaphore = new SemaphoreSlim(Math.Max(1, GitChangesOptions.Value.MaxParallelRepositoryOperations));

            var tasks = targets.Select(async repo =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var resolved = await ResolveRepositoryAsync(repo.WorkspaceRepositoryId);
                    if (resolved == null)
                    {
                        return;
                    }

                    var result = await AgentClient.DiscardAsync(
                        resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName,
                        GitChangeOperationScope.Repository, [], ct);

                    await PersistMutationResultAsync(repo.WorkspaceRepositoryId, resolved.Value.RepositoryId, result.Success, result.Snapshot, result.ErrorMessage, reload: false);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Discard-all-changed failed for repository {RepositoryName}", repo.RepositoryName);
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completed);
                    job.ReportProgress($"Undone {done} of {targets.Count} repositories...");
                }
            });

            await Task.WhenAll(tasks);
            await Task.Delay(150, ct);
        });
    }

    /// <summary>Undo for every currently multi-selected unstaged File row - the discard equivalent of
    /// <see cref="RunSelectedSectionMutationAsync"/>. Multi-selection only ever contains unstaged rows for
    /// discard since the undo icon does not exist on Staged rows, so no section filter is needed here.</summary>
    private void ConfirmDiscardSelected()
    {
        if (!AgentBridge.IsAgentConnected)
        {
            ToastService.ShowError("Agent not connected. Start GrayMoon.Agent and try again.");
            return;
        }

        if (IsJobRunning)
        {
            return;
        }

        var targets = _rows
            .Where(r => r.Kind == GitChangesTreeRowKind.File && !r.IsStagedSection && _multiSelectedKeys.Contains(r.Key))
            .GroupBy(r => r.WorkspaceRepositoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.FilePath!).ToList());

        if (targets.Count == 0)
        {
            return;
        }

        var fileCount = targets.Sum(t => t.Value.Count);
        var message = $"Undo changes to {fileCount} selected file{(fileCount == 1 ? "" : "s")}?";
        var details = targets
            .Select(kvp =>
            {
                var repoName = _view?.Repositories.FirstOrDefault(r => r.WorkspaceRepositoryId == kvp.Key)?.RepositoryName
                    ?? $"repo {kvp.Key}";
                return new ConfirmDetailGroup(repoName, CapDetailItems(kvp.Value));
            })
            .ToList();

        ShowConfirm(message, () =>
        {
            RunDiscardSelectedJob(targets);
            return Task.CompletedTask;
        }, "Undo", details, UndoFooterNote, confirmIsDanger: true);
    }

    private void RunDiscardSelectedJob(Dictionary<int, IReadOnlyList<string>> targets)
    {
        if (IsJobRunning)
        {
            return;
        }

        var fileCount = targets.Sum(t => t.Value.Count);
        var label = $"Undoing {fileCount} selected file{(fileCount == 1 ? "" : "s")}...";

        StartPageJob(label, async (job, ct) =>
        {
            var completed = 0;
            using var semaphore = new SemaphoreSlim(Math.Max(1, GitChangesOptions.Value.MaxParallelRepositoryOperations));

            var tasks = targets.Select(async kvp =>
            {
                var (workspaceRepositoryId, paths) = (kvp.Key, kvp.Value);
                await semaphore.WaitAsync(ct);
                try
                {
                    var resolved = await ResolveRepositoryAsync(workspaceRepositoryId);
                    if (resolved == null)
                    {
                        return;
                    }

                    var result = await AgentClient.DiscardAsync(
                        resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName,
                        GitChangeOperationScope.ExplicitPaths, paths, ct);

                    await PersistMutationResultAsync(workspaceRepositoryId, resolved.Value.RepositoryId, result.Success, result.Snapshot, result.ErrorMessage, reload: false);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Multi-select discard failed for workspace-repository {WorkspaceRepositoryId}", workspaceRepositoryId);
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completed);
                    job.ReportProgress($"Undone {done} of {targets.Count} repositories...");
                }
            });

            await Task.WhenAll(tasks);

            SafeInvoke(ClearMultiSelection);

            await Task.Delay(150, ct);
        });
    }

    private sealed record ConfirmModalState
    {
        public bool IsVisible { get; init; }
        public string Message { get; init; } = "";
        public string ButtonText { get; init; } = "Yes";
        public IReadOnlyList<ConfirmDetailGroup> Details { get; init; } = Array.Empty<ConfirmDetailGroup>();
        public string? FooterNote { get; init; }
        public bool ConfirmIsDanger { get; init; }
        public Func<Task>? PendingAction { get; init; }
    }
}
