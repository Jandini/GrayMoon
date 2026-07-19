using GrayMoon.App.Models;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    [Inject] private IOptions<WorkspaceOptions> WorkspaceOptions { get; set; } = default!;

    private string _workspaceCommitMessage = string.Empty;
    private bool _workspaceCommitMessageHasContent;

    /// <summary>
    /// Restores the in-progress commit message for the current WorkspaceId from the circuit-scoped
    /// memory. Called on initial load and whenever the page switches to a different workspace, so
    /// navigating away and back (or between workspaces) never loses an unsubmitted message.
    /// </summary>
    private void RestoreWorkspaceCommitMessage()
    {
        _workspaceCommitMessage = CommitMessageMemory.Get(WorkspaceId);
        _workspaceCommitMessageHasContent = !string.IsNullOrWhiteSpace(_workspaceCommitMessage);
    }

    private void OnWorkspaceCommitMessageInput(ChangeEventArgs e)
    {
        var newValue = e.Value?.ToString() ?? string.Empty;
        var hadContent = _workspaceCommitMessageHasContent;
        var hasContent = !string.IsNullOrWhiteSpace(newValue);
        _workspaceCommitMessage = newValue;
        _workspaceCommitMessageHasContent = hasContent;
        CommitMessageMemory.Set(WorkspaceId, newValue);

        if (hadContent != hasContent)
        {
            StateHasChanged();
        }
    }

    private int StagedRepositoryCount => _view?.Repositories.Count(r => r.StagedCount > 0) ?? 0;
    private int ChangedRepositoryCount => _view?.Repositories.Count(r => r.StagedCount > 0 || r.ChangedCount > 0) ?? 0;

    /// <summary>
    /// Commits with one shared message across every applicable repository. Not atomic: each repository
    /// commits independently through the bounded fan-out below, one repository's failure never blocks
    /// or rolls back another's success. Mirrors the existing SemaphoreSlim + Task.WhenAll idiom used by
    /// PushOrchestrator/DependencyUpdateOrchestrator rather than introducing a new scheduler abstraction.
    /// Runs behind the page's LoadingOverlay/terminal job - this can touch many files across many
    /// repositories and run commit hooks, unlike the fast single-file/folder stage/unstage actions.
    /// </summary>
    private void CommitWorkspaceAsync(bool stagedOnly)
    {
        if (string.IsNullOrWhiteSpace(_workspaceCommitMessage))
        {
            ToastService.ShowError("Enter a commit message.");
            return;
        }

        if (!AgentBridge.IsAgentConnected)
        {
            ToastService.ShowError("Agent not connected. Start GrayMoon.Agent and try again.");
            return;
        }

        if (IsJobRunning || _defaultBranchWarningModal.IsVisible)
        {
            return;
        }

        var targets = (_view?.Repositories ?? [])
            .Where(r => stagedOnly ? r.StagedCount > 0 : (r.StagedCount > 0 || r.ChangedCount > 0))
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        var reposOnDefaultBranch = targets
            .Where(r => !string.IsNullOrWhiteSpace(r.DefaultBranchName)
                && string.Equals(r.BranchName, r.DefaultBranchName, StringComparison.Ordinal))
            .ToList();

        if (reposOnDefaultBranch.Count > 0)
        {
            var repoItems = reposOnDefaultBranch
                .Select(r => new DefaultBranchWarningItem(r.RepositoryName, r.DefaultBranchName!))
                .ToList();
            ShowDefaultBranchWarning(
                "The following repositories are on their default branch. Committing will write directly to the default (protected) branch.",
                repoItems,
                () =>
                {
                    CommitWorkspaceCoreAsync(stagedOnly, targets);
                    return Task.CompletedTask;
                });
            return;
        }

        CommitWorkspaceCoreAsync(stagedOnly, targets);
    }

    private void CommitWorkspaceCoreAsync(bool stagedOnly, List<WorkspaceGitChangesRepositoryView> targets)
    {
        if (IsJobRunning)
        {
            return;
        }

        var message = _workspaceCommitMessage;
        var label = targets.Count == 1
            ? $"Committing in {targets[0].RepositoryName}..."
            : $"Committing in {targets.Count} repositories...";

        StartPageJob(label, async (job, ct) =>
        {
            var succeeded = new List<string>();
            var failed = new List<(string Repository, string Error)>();
            var completed = 0;

            using var semaphore = new SemaphoreSlim(Math.Max(1, WorkspaceOptions.Value.MaxParallelOperations));

            var tasks = targets.Select(async repo =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var resolved = await ResolveRepositoryAsync(repo.WorkspaceRepositoryId);
                    if (resolved == null)
                    {
                        lock (failed)
                        {
                            failed.Add((repo.RepositoryName, "Repository not found or workspace root is not configured."));
                        }

                        return;
                    }

                    var result = await AgentClient.CommitAsync(
                        resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName,
                        message, stageAllFirst: !stagedOnly, ct);

                    await PersistMutationResultAsync(repo.WorkspaceRepositoryId, resolved.Value.RepositoryId, result.Success, result.Snapshot, result.ErrorMessage, reload: false);

                    lock (failed)
                    {
                        if (result.Success)
                        {
                            succeeded.Add(repo.RepositoryName);
                        }
                        else
                        {
                            failed.Add((repo.RepositoryName, result.ErrorMessage ?? "Commit failed."));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Workspace commit failed for repository {RepositoryName}", repo.RepositoryName);
                    lock (failed)
                    {
                        failed.Add((repo.RepositoryName, "Unexpected error - see logs."));
                    }
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completed);
                    job.ReportProgress($"Committed {done} of {targets.Count} repositories...");
                }
            });

            await Task.WhenAll(tasks);

            if (succeeded.Count > 0)
            {
                SafeInvoke(() =>
                {
                    _workspaceCommitMessage = string.Empty;
                    _workspaceCommitMessageHasContent = false;
                    CommitMessageMemory.Clear(WorkspaceId);
                });
            }

            SafeInvoke(() => ToastService.Show(failed.Count == 0
                ? $"Committed in {succeeded.Count} repositor{(succeeded.Count == 1 ? "y" : "ies")}."
                : $"Committed in {succeeded.Count} repositor{(succeeded.Count == 1 ? "y" : "ies")}. Failed in {failed.Count}: {string.Join(", ", failed.Select(f => f.Repository))}."));

            if (failed.Count > 0)
            {
                foreach (var (repository, error) in failed)
                {
                    Logger.LogWarning("Workspace commit failed for {Repository}: {Error}", repository, error);
                }
            }

            // The write queue processes on a background worker; give it a moment before the job's
            // final reload so the page reflects the just-persisted state rather than racing the write.
            await Task.Delay(150, ct);
        });
    }

    private void StageAllChangedAsync() => BulkSectionActionAsync(unstageStagedSection: false);

    private void UnstageAllStagedAsync() => BulkSectionActionAsync(unstageStagedSection: true);

    /// <summary>Stage all items in the Changed section, or unstage all items in the Staged section, across
    /// every repository represented in that section. Same bounded fan-out idiom as <see cref="CommitWorkspaceAsync"/>,
    /// also behind the page's LoadingOverlay/terminal job since it spans every repository in the section.</summary>
    private void BulkSectionActionAsync(bool unstageStagedSection)
    {
        if (!AgentBridge.IsAgentConnected)
        {
            ToastService.ShowError("Agent not connected. Start GrayMoon.Agent and try again.");
            return;
        }

        var targets = (_view?.Repositories ?? [])
            .Where(r => unstageStagedSection ? r.StagedCount > 0 : r.ChangedCount > 0)
            .ToList();

        if (targets.Count == 0 || IsJobRunning)
        {
            return;
        }

        var label = unstageStagedSection
            ? $"Unstaging {targets.Count} repositories..."
            : $"Staging {targets.Count} repositories...";

        StartPageJob(label, async (job, ct) =>
        {
            var completed = 0;
            using var semaphore = new SemaphoreSlim(Math.Max(1, WorkspaceOptions.Value.MaxParallelOperations));

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

                    var result = unstageStagedSection
                        ? await AgentClient.UnstageAsync(resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName, GitChangeOperationScope.Repository, [], ct)
                        : await AgentClient.StageAsync(resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName, GitChangeOperationScope.Repository, [], ct);

                    await PersistMutationResultAsync(repo.WorkspaceRepositoryId, resolved.Value.RepositoryId, result.Success, result.Snapshot, result.ErrorMessage, reload: false);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Bulk section {Action} failed for repository {RepositoryName}", unstageStagedSection ? "unstage" : "stage", repo.RepositoryName);
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completed);
                    job.ReportProgress(unstageStagedSection
                        ? $"Unstaged {done} of {targets.Count} repositories..."
                        : $"Staged {done} of {targets.Count} repositories...");
                }
            });

            await Task.WhenAll(tasks);
            await Task.Delay(150, ct);
        });
    }
}
