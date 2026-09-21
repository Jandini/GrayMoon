using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private async Task ShowConfirmReturnToDefaultLevel(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0)
            return;

        if (_isFeatureContext)
        {
            ToastService.Show(
                "Return to Default is Workspace-only. After merges, refresh versions from default and Update Dependencies, or Remove Feature when done.");
            return;
        }

        var freshLinks = await GetFreshLinkStatesAsync(repositoryIds.Distinct().ToList());
        var nonDefaultRepoIds = freshLinks.Values
            .Where(s => s.NeedsReturnToDefault)
            .Select(s => s.Link.RepositoryId)
            .ToList();

        if (nonDefaultRepoIds.Count == 0)
        {
            ToastService.Show("All repositories in this level are already on the default branch.");
            return;
        }

        await CheckBranchesAndConfirmReturnToDefaultLevel(nonDefaultRepoIds);
    }

    private async Task CheckBranchesAndConfirmReturnToDefaultLevel(List<int> repositoryIds)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0 || IsJobRunning)
            return;

        JobService.StartJob(PageJobKey,
            repositoryIds.Count == 1 ? "Fetching latest branch state..." : $"Fetching latest branch state for {repositoryIds.Count} repositories...",
            async (job, ct) =>
            {
                try
                {
                    var plan = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, ReturnToDefaultPlan>(
                        svc => svc.AnalyzeReturnToDefaultAsync(WorkspaceId, RequireSelectedContextId(), repositoryIds, job.ToOperationProgress(), ct));

                    if (plan.AnalysisFailed)
                    {
                        SafeInvoke(() => ToastService.ShowError(plan.AnalysisError ?? "Failed to prepare return to default."));
                        return;
                    }

                    // Level policy: skip repos that need explicit discard confirmation; only confirm/execute the rest.
                    var blocked = plan.Repositories
                        .Where(r => !r.IsAlreadyOnDefault && r.RequiresExplicitDiscardConfirmation)
                        .ToList();
                    foreach (var r in blocked)
                        SafeInvoke(() => ToastService.Show($"{r.RepositoryName}: skipped return to default (commits ahead of default, PR not merged)."));

                    var safeRepos = plan.Repositories
                        .Where(r => !r.IsAlreadyOnDefault && !r.IsOnTag && !r.RequiresExplicitDiscardConfirmation)
                        .ToList();

                    if (safeRepos.Count == 0)
                    {
                        if (blocked.Count == 0)
                            SafeInvoke(() => ToastService.Show("No repositories to sync."));
                        return;
                    }

                    var safeCount = safeRepos.Count;
                    var dialogMessage = safeCount == 1
                        ? "This will checkout the default branch, remove the current branch locally, and pull the latest. Uncommitted local changes can block checkout."
                        : $"This will return {safeCount} repositories to their default branch: checkout default, remove the current branch locally, and pull. Uncommitted local changes can block checkout for that repo.";

                    // Level dialog historically omitted ahead counts (safe set only), so countdown stays off.
                    var repoItems = safeRepos
                        .Select(r => new ReturnToDefaultRepoItem(
                            r.RepositoryName,
                            r.CurrentBranch ?? "",
                            r.RemoteBranchCanBeDeleted,
                            PrState: null,
                            CommitsAhead: 0))
                        .ToList();
                    var safeRepoIds = safeRepos.Select(r => r.RepositoryId).ToList();

                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await RefreshFromSync();
                        ShowReturnToDefaultOptions(dialogMessage, repoItems, (deleteRemote, allowForce) =>
                            ReturnToDefaultLevelAsync(safeRepoIds, deleteRemote, allowForce));
                        StateHasChanged();
                    });
                }
                catch (OperationCanceledException)
                {
                    SafeInvoke(() => ToastService.Show("Fetch cancelled."));
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error checking branches for return to default");
                    SafeInvoke(() => ToastService.ShowError("Failed to prepare return to default."));
                    throw;
                }
            });
    }

    private async Task ReturnToDefaultFromModalAsync((int RepositoryId, string? RepositoryName, string CurrentBranchName, string DefaultBranch) request)
    {
        var (repositoryId, repositoryName, currentBranchName, defaultBranch) = request;
        if (workspace == null || IsJobRunning)
            return;
        if (_isFeatureContext)
        {
            ToastService.Show(
                "Return to Default is Workspace-only. Switch to Workspace, or Remove Feature when finished.");
            CloseSwitchBranchModal();
            return;
        }
        if (string.IsNullOrWhiteSpace(repositoryName))
            return;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            CloseSwitchBranchModal();
            return;
        }

        CloseSwitchBranchModal();

        try
        {
            var plan = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, ReturnToDefaultPlan>(
                svc => svc.AnalyzeReturnToDefaultAsync(WorkspaceId, RequireSelectedContextId(), [repositoryId], progress: null, CancellationToken.None));

            if (plan.AnalysisFailed)
            {
                ToastService.ShowError(plan.AnalysisError ?? "Failed to prepare return to default.");
                return;
            }

            var repo = plan.Repositories.FirstOrDefault(r => r.RepositoryId == repositoryId);
            if (repo == null || repo.IsAlreadyOnDefault)
            {
                ToastService.Show("Repository is already on the default branch.");
                return;
            }

            if (repo.IsOnTag)
            {
                ToastService.Show(TagBlockedActionMessage);
                return;
            }

            // Single-repo policy: skip when ahead without merged/closed PR (do not offer the destructive dialog).
            if (repo.RequiresExplicitDiscardConfirmation)
            {
                ToastService.Show("Skipped return to default: commits ahead of default branch and PR is not merged.");
                return;
            }

            var branchName = repo.CurrentBranch ?? currentBranchName;
            if (repo.RemoteBranchCanBeDeleted || repo.CommitsAheadOfDefault > 0)
            {
                ShowReturnToDefaultOptions(
                    "This will checkout the default branch, remove the current branch locally, and pull the latest.",
                    [new ReturnToDefaultRepoItem(repositoryName!, branchName, repo.RemoteBranchCanBeDeleted, repo.PullRequestState, repo.CommitsAheadOfDefault)],
                    (deleteRemote, allowForce) => ReturnToDefaultSingleRepoAfterCheckAsync(
                        repositoryId,
                        repositoryName,
                        branchName,
                        deleteRemote && repo.RemoteBranchCanBeDeleted,
                        defaultBranch,
                        allowForce));
            }
            else
            {
                await ReturnToDefaultSingleRepoAfterCheckAsync(
                    repositoryId,
                    repositoryName,
                    branchName,
                    deleteRemoteBranch: false,
                    defaultBranch,
                    allowForceDeleteLocalBranch: true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error preparing return to default (repository {RepositoryId})", repositoryId);
            ToastService.ShowError("Failed to prepare return to default.");
        }
    }

    private Task ReturnToDefaultSingleRepoAfterCheckAsync(
        int repositoryId,
        string repositoryName,
        string currentBranchName,
        bool deleteRemoteBranch = false,
        string? defaultBranchName = null,
        bool allowForceDeleteLocalBranch = true)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        var message = string.IsNullOrWhiteSpace(defaultBranchName)
            ? "Returning to default branch..."
            : $"Returning to {defaultBranchName}...";

        StartPageJob(message, async (job, ct) =>
        {
            var options = new ReturnToDefaultOptions(
                DeleteRemoteBranch: deleteRemoteBranch,
                AllowForceDeleteLocalBranch: allowForceDeleteLocalBranch,
                CloseOpenPullRequest: false);

            var result = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, OperationResult>(
                svc => svc.ExecuteReturnToDefaultAsync(WorkspaceId, RequireSelectedContextId(), [repositoryId], options, job.ToOperationProgress(), ct));

            if (result.Success)
            {
                SafeInvoke(() => repositoryErrors.Remove(repositoryId));
                await InvokeAsync(async () => { if (_disposed) return; await RefreshFromSync(); });
            }
            else if (result.RepoErrors != null && result.RepoErrors.TryGetValue(repositoryId, out var errMsg))
            {
                SafeInvoke(() => SetRepositoryError(repositoryId, errMsg));
            }
            else if (!string.IsNullOrWhiteSpace(result.Error))
            {
                SafeInvoke(() => SetRepositoryError(repositoryId, result.Error));
            }
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error returning to default branch for repository {RepositoryId}", repositoryId);
                SafeInvoke(() => SetRepositoryError(repositoryId, "An error occurred while returning to default branch. The GrayMoon Agent may be offline."));
            }
        });

        return Task.CompletedTask;
    }

    private Task ReturnToDefaultLevelAsync(List<int> repositoryIds, bool deleteRemoteBranch = false, bool allowForceDeleteLocalBranch = true)
    {
        if (workspace == null || repositoryIds == null || repositoryIds.Count == 0 || IsJobRunning)
            return Task.CompletedTask;

        repositoryIds = repositoryIds.Where(id => !IsRepoOnTag(id)).ToList();
        if (repositoryIds.Count == 0)
        {
            ToastService.Show("All repositories are on tags; checkout a branch first.");
            return Task.CompletedTask;
        }

        StartPageJob("Returning to default branch...", async (job, ct) =>
        {
            var options = new ReturnToDefaultOptions(
                DeleteRemoteBranch: deleteRemoteBranch,
                AllowForceDeleteLocalBranch: allowForceDeleteLocalBranch,
                CloseOpenPullRequest: false);

            var result = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, OperationResult>(
                svc => svc.ExecuteReturnToDefaultAsync(WorkspaceId, RequireSelectedContextId(), repositoryIds, options, job.ToOperationProgress(), ct));

            SafeInvoke(() =>
            {
                if (result.RepoErrors is { Count: > 0 })
                {
                    foreach (var (repoId, errMsg) in result.RepoErrors)
                        SetRepositoryError(repoId, errMsg);
                }

                foreach (var repoId in repositoryIds)
                {
                    if (result.RepoErrors == null || !result.RepoErrors.ContainsKey(repoId))
                        repositoryErrors.Remove(repoId);
                }
            });
        }, new PageJobOptions
        {
            OnError = ex =>
            {
                Logger.LogError(ex, "Error returning to default branch for level");
                SafeInvoke(() => SetPageError("An error occurred while returning to default branch. The GrayMoon Agent may be offline."));
            }
        });

        return Task.CompletedTask;
    }

    private async Task ReturnAllToDefaultAsync()
    {
        if (workspace == null || IsJobRunning)
            return;

        if (_isFeatureContext)
        {
            ToastService.Show(
                "Return to Default is Workspace-only. After merges, refresh versions from default and Update Dependencies, or Remove Feature when done.");
            return;
        }

        var allLinks = await GetAllLinksForOperationAsync();
        var eligibleRepos = allLinks
            .Where(wr =>
                !wr.IsOnTag &&
                !string.IsNullOrWhiteSpace(wr.BranchName) &&
                !string.IsNullOrWhiteSpace(wr.DefaultBranchName) &&
                !string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal))
            .ToList();

        if (eligibleRepos.Count == 0)
        {
            ToastService.Show("All repositories are already on the default branch.");
            return;
        }

        var eligibleIds = eligibleRepos.Select(wr => wr.RepositoryId).ToList();

        var totalCount = eligibleIds.Count;
        var dialogMessage = totalCount == 1
            ? "This will checkout the default branch, remove the current branch locally, and pull the latest. Uncommitted local changes can block checkout."
            : $"This will return {totalCount} repositories to their default branch: checkout default, remove the current branch locally, and pull. Uncommitted local changes can block checkout for that repo.";

        JobService.StartJob(PageJobKey,
            totalCount == 1 ? "Fetching latest branch state..." : $"Fetching latest branch state for {totalCount} repositories...",
            async (job, ct) =>
            {
                try
                {
                    var plan = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, ReturnToDefaultPlan>(
                        svc => svc.AnalyzeReturnToDefaultAsync(WorkspaceId, RequireSelectedContextId(), eligibleIds, job.ToOperationProgress(), ct));

                    if (plan.AnalysisFailed)
                    {
                        SafeInvoke(() => ToastService.ShowError(plan.AnalysisError ?? "Failed to prepare return to default."));
                        return;
                    }

                    var actionable = plan.Repositories
                        .Where(r => !r.IsAlreadyOnDefault && !r.IsOnTag)
                        .ToList();

                    if (actionable.Count == 0)
                    {
                        SafeInvoke(() => ToastService.Show("All repositories are already on the default branch."));
                        return;
                    }

                    var repoItems = actionable
                        .Select(r => new ReturnToDefaultRepoItem(
                            r.RepositoryName,
                            r.CurrentBranch ?? "",
                            r.RemoteBranchCanBeDeleted,
                            r.PullRequestState,
                            r.CommitsAheadOfDefault))
                        .ToList();
                    var repoIds = actionable.Select(r => r.RepositoryId).ToList();

                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await RefreshFromSync();
                        ShowReturnToDefaultOptions(dialogMessage, repoItems, (deleteRemote, allowForce) =>
                            ExecuteReturnAllToDefaultAsync(repoIds, deleteRemote, allowForce));
                        StateHasChanged();
                    });
                }
                catch (OperationCanceledException)
                {
                    SafeInvoke(() => ToastService.Show("Fetch cancelled."));
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error fetching branch state before return all to default");
                    SafeInvoke(() => ToastService.ShowError("Failed to prepare return to default."));
                    throw;
                }
            });
    }

    private Task ExecuteReturnAllToDefaultAsync(
        IReadOnlyList<int> repositoryIds,
        bool deleteRemoteBranch,
        bool allowForceDeleteLocalBranch)
    {
        if (workspace == null || repositoryIds.Count == 0 || IsJobRunning)
            return Task.CompletedTask;

        var total = repositoryIds.Count;
        StartPageJob("Returning to default branch...", async (job, ct) =>
        {
            var options = new ReturnToDefaultOptions(
                DeleteRemoteBranch: deleteRemoteBranch,
                AllowForceDeleteLocalBranch: allowForceDeleteLocalBranch,
                CloseOpenPullRequest: true);

            var result = await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations, OperationResult>(
                svc => svc.ExecuteReturnToDefaultAsync(WorkspaceId, RequireSelectedContextId(), repositoryIds, options, job.ToOperationProgress(), ct));

            var failureCount = result.RepoErrors?.Count ?? 0;
            var successCount = total - failureCount;

            SafeInvoke(() =>
            {
                if (result.RepoErrors is { Count: > 0 })
                {
                    foreach (var (repoId, errMsg) in result.RepoErrors)
                        SetRepositoryError(repoId, errMsg);
                }

                foreach (var repoId in repositoryIds)
                {
                    if (result.RepoErrors == null || !result.RepoErrors.ContainsKey(repoId))
                        repositoryErrors.Remove(repoId);
                }

                if (total > 1 && failureCount == 0)
                    ToastService.Show($"Returned {successCount} of {total} repositories to default branch.");
            });
        }, new PageJobOptions
        {
            OnError = ex =>
            {
                Logger.LogError(ex, "Error returning all repositories to default branch");
                SafeInvoke(() => SetPageError("An error occurred while returning to default branch. The GrayMoon Agent may be offline."));
            }
        });

        return Task.CompletedTask;
    }
}
