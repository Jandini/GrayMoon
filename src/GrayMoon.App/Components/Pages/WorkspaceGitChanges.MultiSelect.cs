using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    private readonly HashSet<string> _multiSelectedKeys = [];
    private string? _multiSelectAnchorKey;

    private bool IsMultiSelected(GitChangesTreeRow row) => _multiSelectedKeys.Contains(row.Key);

    private void ClearMultiSelection()
    {
        _multiSelectedKeys.Clear();
        _multiSelectAnchorKey = null;
    }

    private void StageSelectedAsync() => RunSelectedSectionMutationAsync(unstage: false);

    private void UnstageSelectedAsync() => RunSelectedSectionMutationAsync(unstage: true);

    /// <summary>
    /// Stages/unstages every currently multi-selected File row that belongs to the section matching the
    /// clicked action (Changed for Stage, Staged for Unstage) - rows selected in the other section are
    /// ignored for this click. Groups the selected paths by repository and fans out with the same bounded
    /// concurrency + LoadingOverlay job pattern as <see cref="BulkSectionActionAsync"/>.
    /// </summary>
    private void RunSelectedSectionMutationAsync(bool unstage)
    {
        if (!WorkerBridge.IsWorkerConnected)
        {
            ToastService.ShowError("Worker not connected. Start the GrayMoon Worker and try again.");
            return;
        }

        if (IsJobRunning)
        {
            return;
        }

        var targets = _rows
            .Where(r => r.Kind == GitChangesTreeRowKind.File
                && r.IsStagedSection == unstage
                && _multiSelectedKeys.Contains(r.Key))
            .GroupBy(r => r.WorkspaceRepositoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.FilePath!).ToList());

        if (targets.Count == 0)
        {
            return;
        }

        var fileCount = targets.Sum(t => t.Value.Count);
        var label = unstage
            ? $"Unstaging {fileCount} selected file{(fileCount == 1 ? "" : "s")}..."
            : $"Staging {fileCount} selected file{(fileCount == 1 ? "" : "s")}...";

        var contextId = RequireSelectedContextId();
        StartPageJob(label, async (job, ct) =>
        {
            string NameOf(int id) =>
                _view?.Repositories.FirstOrDefault(r => r.WorkspaceRepositoryId == id)?.RepositoryName ?? $"repository {id}";

            var outcomes = new RepositoryOutcomes(targets.Keys.Select(id => (id, NameOf(id))));
            var completed = 0;
            using var semaphore = new SemaphoreSlim(Math.Max(1, GitChangesOptions.Value.MaxParallelRepositoryOperations));

            var tasks = targets.Select(async kvp =>
            {
                var (workspaceRepositoryId, paths) = (kvp.Key, kvp.Value);
                var name = NameOf(workspaceRepositoryId);
                try
                {
                    await semaphore.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    outcomes.Failed(workspaceRepositoryId, name, "Cancelled before it started.");
                    throw;
                }

                try
                {
                    var resolved = await ResolveRepositoryAsync(workspaceRepositoryId);
                    if (resolved == null)
                    {
                        outcomes.Failed(workspaceRepositoryId, name, "Repository not found or workspace root is not configured.");
                        return;
                    }

                    var result = unstage
                        ? await GitChangesOperations.UnstageAsync(WorkspaceId, contextId, resolved.Value.RepositoryId, GitChangeOperationScope.ExplicitPaths, paths, ct)
                        : await GitChangesOperations.StageAsync(WorkspaceId, contextId, resolved.Value.RepositoryId, GitChangeOperationScope.ExplicitPaths, paths, ct);

                    await PersistMutationResultAsync(workspaceRepositoryId, resolved.Value.RepositoryId, result.Success, result.Snapshot, result.ErrorMessage, reload: false, showError: false);

                    if (result.Success)
                        outcomes.Succeeded(workspaceRepositoryId, name);
                    else
                        outcomes.Failed(workspaceRepositoryId, name, result.ErrorMessage ?? "Operation failed.");
                }
                catch (OperationCanceledException)
                {
                    outcomes.Failed(workspaceRepositoryId, name, "Cancelled.");
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Multi-select {Action} failed for workspace-repository {WorkspaceRepositoryId}", unstage ? "unstage" : "stage", workspaceRepositoryId);
                    outcomes.Failed(workspaceRepositoryId, name, $"Unexpected error ({ex.GetType().Name}) - see logs.");
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completed);
                    job.ReportProgress(unstage
                        ? $"Unstaged {done} of {targets.Count} repositories..."
                        : $"Staged {done} of {targets.Count} repositories...");
                }
            });

            await Task.WhenAll(tasks);

            ReportBulkOutcomes(job, outcomes, unstage ? "Unstaged" : "Staged", unstage ? "Not unstaged" : "Not staged");
            SafeInvoke(ClearMultiSelection);
        });
    }
}
