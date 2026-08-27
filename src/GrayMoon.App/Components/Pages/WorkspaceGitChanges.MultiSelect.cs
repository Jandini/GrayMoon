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

                    var result = unstage
                        ? await AgentClient.UnstageAsync(resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName, GitChangeOperationScope.ExplicitPaths, paths, ct)
                        : await AgentClient.StageAsync(resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName, GitChangeOperationScope.ExplicitPaths, paths, ct);

                    await PersistMutationResultAsync(workspaceRepositoryId, resolved.Value.RepositoryId, result.Success, result.Snapshot, result.ErrorMessage, reload: false);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Multi-select {Action} failed for workspace-repository {WorkspaceRepositoryId}", unstage ? "unstage" : "stage", workspaceRepositoryId);
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

            SafeInvoke(ClearMultiSelection);

            // Give the write queue a moment to flush before the job's own reload runs.
            await Task.Delay(150, ct);
        });
    }
}
