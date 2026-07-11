using GrayMoon.App.Models;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    [Inject] private IOptions<WorkspaceOptions> WorkspaceOptions { get; set; } = default!;

    private string _workspaceCommitMessage = string.Empty;
    private bool _workspaceCommitMessageHasContent;
    private bool _isWorkspaceCommitRunning;

    private void OnWorkspaceCommitMessageInput(ChangeEventArgs e)
    {
        var newValue = e.Value?.ToString() ?? string.Empty;
        var hadContent = _workspaceCommitMessageHasContent;
        var hasContent = !string.IsNullOrWhiteSpace(newValue);
        _workspaceCommitMessage = newValue;
        _workspaceCommitMessageHasContent = hasContent;

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
    /// </summary>
    private async Task CommitWorkspaceAsync(bool stagedOnly)
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

        var targets = (_view?.Repositories ?? [])
            .Where(r => stagedOnly ? r.StagedCount > 0 : (r.StagedCount > 0 || r.ChangedCount > 0))
            .ToList();

        if (targets.Count == 0 || _isWorkspaceCommitRunning)
        {
            return;
        }

        _isWorkspaceCommitRunning = true;
        StateHasChanged();

        var message = _workspaceCommitMessage;
        var succeeded = new List<string>();
        var failed = new List<(string Repository, string Error)>();

        try
        {
            using var semaphore = new SemaphoreSlim(Math.Max(1, WorkspaceOptions.Value.MaxParallelOperations));

            var tasks = targets.Select(async repo =>
            {
                await semaphore.WaitAsync();
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
                        message, stageAllFirst: !stagedOnly, CancellationToken.None);

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
                }
            });

            await Task.WhenAll(tasks);
        }
        finally
        {
            _isWorkspaceCommitRunning = false;
        }

        if (succeeded.Count > 0)
        {
            _workspaceCommitMessage = string.Empty;
            _workspaceCommitMessageHasContent = false;
        }

        ToastService.Show(failed.Count == 0
            ? $"Committed in {succeeded.Count} repositor{(succeeded.Count == 1 ? "y" : "ies")}."
            : $"Committed in {succeeded.Count} repositor{(succeeded.Count == 1 ? "y" : "ies")}. Failed in {failed.Count}: {string.Join(", ", failed.Select(f => f.Repository))}.");

        if (failed.Count > 0)
        {
            foreach (var (repository, error) in failed)
            {
                Logger.LogWarning("Workspace commit failed for {Repository}: {Error}", repository, error);
            }
        }

        await Task.Delay(150);
        await LoadAsync();
    }

    private Task StageAllChangedAsync() => BulkSectionActionAsync(unstageStagedSection: false);

    private Task UnstageAllStagedAsync() => BulkSectionActionAsync(unstageStagedSection: true);

    /// <summary>Stage all items in the Changed section, or unstage all items in the Staged section, across
    /// every repository represented in that section. Same bounded fan-out idiom as <see cref="CommitWorkspaceAsync"/>.</summary>
    private async Task BulkSectionActionAsync(bool unstageStagedSection)
    {
        if (!AgentBridge.IsAgentConnected)
        {
            ToastService.ShowError("Agent not connected. Start GrayMoon.Agent and try again.");
            return;
        }

        var targets = (_view?.Repositories ?? [])
            .Where(r => unstageStagedSection ? r.StagedCount > 0 : r.ChangedCount > 0)
            .ToList();

        if (targets.Count == 0 || _isWorkspaceCommitRunning)
        {
            return;
        }

        _isWorkspaceCommitRunning = true;
        StateHasChanged();

        try
        {
            using var semaphore = new SemaphoreSlim(Math.Max(1, WorkspaceOptions.Value.MaxParallelOperations));

            var tasks = targets.Select(async repo =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var resolved = await ResolveRepositoryAsync(repo.WorkspaceRepositoryId);
                    if (resolved == null)
                    {
                        return;
                    }

                    var result = unstageStagedSection
                        ? await AgentClient.UnstageAsync(resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName, GitChangeOperationScope.Repository, [], CancellationToken.None)
                        : await AgentClient.StageAsync(resolved.Value.Root, resolved.Value.WorkspaceName, resolved.Value.RepositoryName, GitChangeOperationScope.Repository, [], CancellationToken.None);

                    await PersistMutationResultAsync(repo.WorkspaceRepositoryId, resolved.Value.RepositoryId, result.Success, result.Snapshot, result.ErrorMessage, reload: false);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Bulk section {Action} failed for repository {RepositoryName}", unstageStagedSection ? "unstage" : "stage", repo.RepositoryName);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        finally
        {
            _isWorkspaceCommitRunning = false;
        }

        await Task.Delay(150);
        await LoadAsync();
    }
}
