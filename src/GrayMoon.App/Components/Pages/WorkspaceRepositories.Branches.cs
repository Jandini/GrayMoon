using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private SwitchBranchModalState _switchBranchModal = new();
    private BranchModalState _branchModal = new();
    private UpdateBranchModalState _updateBranchModal = new();

    private void ShowSwitchBranchModal(int repositoryId, string? currentBranch, string? cloneUrl)
    {
        var wr = TryGetLink(repositoryId);
        var repo = wr?.Repository;

        if (repo == null)
            return;

        _switchBranchModal = _switchBranchModal with
        {
            IsVisible = true,
            RepositoryId = repositoryId,
            RepositoryName = repo.RepositoryName,
            CurrentBranch = currentBranch,
            RepositoryUrl = cloneUrl ?? repo.CloneUrl
        };
        StateHasChanged();
    }

    private void CloseSwitchBranchModal()
    {
        _switchBranchModal = _switchBranchModal with
        {
            IsVisible = false,
            RepositoryId = 0,
            RepositoryName = null,
            CurrentBranch = null,
            RepositoryUrl = null,
            InitialTab = null
        };
    }

    private void ShowSwitchBranchModalOnTagsTab(WorkspaceRepositoryLink link)
    {
        var wr = TryGetLink(link.RepositoryId);
        var repo = wr?.Repository;
        if (repo == null)
            return;

        _switchBranchModal = _switchBranchModal with
        {
            IsVisible = true,
            RepositoryId = link.RepositoryId,
            RepositoryName = repo.RepositoryName,
            CurrentBranch = null,
            RepositoryUrl = repo.CloneUrl,
            InitialTab = "tags"
        };
        StateHasChanged();
    }

    /// <summary>When every workspace repo has the same non-empty <see cref="WorkspaceRepositoryLink.BranchName"/>, returns that name; otherwise null.</summary>
    private static string? GetUnifiedWorkspaceCurrentBranch(IReadOnlyList<WorkspaceRepositoryLink> links)
    {
        if (links.Count == 0)
            return null;
        string? first = null;
        foreach (var link in links)
        {
            var name = link.BranchName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return null;
            first ??= name;
            if (!string.Equals(first, name, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return first;
    }

    private async Task ShowBranchModalAsync(string initialTab = "newbranch")
    {
        if (workspace == null || !HasRepositories)
            return;
        try
        {
            await LoadCommonBranchesForBranchModalAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load common branches for branch modal");
        }
        var allLinks = await GetAllLinksForOperationAsync();
        _branchModal = _branchModal with
        {
            IsVisible = true,
            WorkspaceUnifiedCurrentBranch = GetUnifiedWorkspaceCurrentBranch(allLinks),
            InitialTab = string.Equals(initialTab, "switchbranch", StringComparison.OrdinalIgnoreCase) ? "switchbranch" : "newbranch"
        };
        StateHasChanged();
    }

    private void CloseBranchModal()
    {
        _branchModal = _branchModal with { IsVisible = false };
    }

    private async Task LoadCommonBranchesForBranchModalAsync(CancellationToken cancellationToken)
    {
        var data = await WorkspaceBranchHandler.GetCommonBranchesAsync(
            WorkspaceId,
            ApiBaseUrl,
            cancellationToken);

        if (data == null)
            return;

        var commonLocal = data.CommonLocalBranchNames ?? data.CommonBranchNames ?? new List<string>();
        var commonRemote = data.CommonRemoteBranchNames ?? new List<string>();
        var allLinks = await GetAllLinksForOperationAsync();
        _branchModal = _branchModal with
        {
            CommonBranchNames = commonLocal,
            CommonLocalBranchNames = commonLocal,
            CommonRemoteBranchNames = commonRemote,
            DefaultDisplayText = data.DefaultDisplayText ?? "multiple",
            WorkspaceUnifiedCurrentBranch = GetUnifiedWorkspaceCurrentBranch(allLinks)
        };
    }

    private async Task FetchCommonBranchesAcrossWorkspaceAsync()
    {
        if (workspace == null || IsJobRunning)
            return;

        var allLinks = await GetAllLinksForOperationAsync();
        var repoIds = allLinks
            .Select(wr => wr.RepositoryId)
            .Distinct()
            .ToList();
        if (repoIds.Count == 0)
            return;

        StartPageJob("Fetching branches...", async (job, ct) =>
        {
            var fetchAllDone = 0;
            var successCount = 0;
            var failureCount = 0;
            using var fetchAllSemaphore = new System.Threading.SemaphoreSlim(8);
            await Task.WhenAll(repoIds.Select(async repoId =>
            {
                await fetchAllSemaphore.WaitAsync(ct);
                try
                {
                    var ok = await ScopedExecutor.ExecuteAsync<WorkspaceGitService, bool>(
                        svc => svc.RefreshBranchesForRepositoryAsync(repoId, WorkspaceId, ct));
                    if (ok) Interlocked.Increment(ref successCount);
                    else Interlocked.Increment(ref failureCount);
                }
                finally
                {
                    fetchAllSemaphore.Release();
                    var c = Interlocked.Increment(ref fetchAllDone);
                    job.ReportProgress($"Fetched branches in {c} of {repoIds.Count} repositories...");
                }
            }));

            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                await RefreshFromSync();
                await LoadCommonBranchesForBranchModalAsync(CancellationToken.None);
                StateHasChanged();
            });

            if (failureCount > 0)
                SafeInvoke(() => ToastService.ShowError($"Fetched branches for {successCount} repositories. {failureCount} failed."));
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            CancelToast = "Fetch branches cancelled.",
            OnError = ex =>
            {
                Logger.LogError(ex, "Error fetching branches across workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => ToastService.ShowError("Failed to fetch branches across workspace."));
            }
        });

    }

    private async Task CheckoutCommonBranchAcrossWorkspaceAsync((string BranchName, bool SkipReposOnTags) args)
    {
        var (branchName, skipReposOnTags) = args;
        if (workspace == null || string.IsNullOrWhiteSpace(branchName) || IsJobRunning)
            return;

        var allLinks = await GetAllLinksForOperationAsync();
        var repoIds = allLinks
            .Where(wr => !skipReposOnTags || !wr.IsOnTag)
            .Select(wr => wr.RepositoryId)
            .Distinct()
            .ToList();
        if (repoIds.Count == 0)
            return;

        StartPageJob("Checking out...", async (job, ct) =>
        {
            var result = await ScopedExecutor.ExecuteAsync<WorkspaceBranchHandler, WorkspaceBranchBulkResult>(
                svc => svc.CheckoutBranchForWorkspaceAsync(
                    WorkspaceId,
                    repoIds,
                    branchName,
                    ApiBaseUrl,
                    (completed, total) =>
                    {
                        job.ReportProgress(completed <= 0
                            ? "Checking out..."
                            : $"Checked out {completed} of {total} branches");
                    },
                    ct));

            SafeInvoke(() =>
            {
                foreach (var repoId in repoIds)
                {
                    if (result.ErrorsByRepositoryId.TryGetValue(repoId, out var error))
                        repositoryErrors[repoId] = error;
                    else
                        repositoryErrors.Remove(repoId);
                }
            });

            if (result.FailureCount > 0)
            {
                var firstError = result.ErrorsByRepositoryId.Values.FirstOrDefault();
                SafeInvoke(() => ToastService.ShowError(
                    !string.IsNullOrWhiteSpace(firstError)
                        ? firstError
                        : $"Checked out branch in {result.SuccessCount} repositories. {result.FailureCount} failed."));
            }
        }, new PageJobOptions
        {
            CancelToast = "Checkout cancelled.",
            OnError = ex =>
            {
                Logger.LogError(ex, "Error checking out branch {BranchName} across workspace {WorkspaceId}", branchName, WorkspaceId);
                SafeInvoke(() => ToastService.ShowError("Failed to check out branch across workspace."));
            }
        });

    }

    private async Task CreateBranchesAsync((string NewBranchName, string BaseBranch, bool SkipReposOnTags) args)
    {
        var (newBranchName, baseBranch, skipReposOnTags) = args;
        if (workspace == null || string.IsNullOrWhiteSpace(newBranchName) || IsJobRunning)
            return;

        CloseBranchModal();
        errorMessage = null;

        var allLinks = await GetAllLinksForOperationAsync();
        var tagFilteredRepoIds = skipReposOnTags
            ? allLinks.Where(wr => !wr.IsOnTag).Select(wr => wr.RepositoryId).ToHashSet()
            : (IReadOnlySet<int>?)null;

        StartPageJob("Creating branches...", async (job, ct) =>
        {
            await ScopedExecutor.ExecuteAsync<WorkspaceBranchHandler>(svc =>
                svc.CreateBranchesAsync(
                    WorkspaceId,
                    newBranchName,
                    baseBranch,
                    tagFilteredRepoIds,
                    (completed, total) => { job.ReportProgress($"Created {completed} of {total} branches"); },
                    syncState: false,
                    cancellationToken: ct));
        }, new PageJobOptions
        {
            RefreshOnCancel = true,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error creating branches for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => errorMessage = "Create branches failed. The GrayMoon Agent may be offline. Start the Agent and try again.");
            }
        });

    }

    private async Task OnBranchChangedAsync()
    {
        // Refresh workspace data to show updated branch
        await RefreshFromSync();
    }

    private Task CreateSingleBranchAsync((int RepositoryId, string? RepositoryName, string NewBranchName, string BaseBranch, bool SetUpstream) request)
    {
        var (repositoryId, repositoryName, newBranchName, baseBranch, setUpstream) = request;
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        CloseSwitchBranchModal();
        errorMessage = null;

        StartPageJob("Creating branch...", async (job, ct) =>
        {
            var (success, err) = await ScopedExecutor.ExecuteAsync<WorkspaceBranchHandler, (bool Success, string? Error)>(
                svc => svc.CreateSingleBranchAsync(WorkspaceId, repositoryId, newBranchName, baseBranch, setUpstream, ApiBaseUrl, ct));

            if (!success)
            {
                SafeInvoke(() => errorMessage = err ?? "Create branch failed. The GrayMoon Agent may be offline. Start the Agent and try again.");
            }
            else
            {
                if (err != null)
                    SafeInvoke(() => errorMessage = err);

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error creating branch for repository {RepositoryId}", repositoryId);
                SafeInvoke(() => errorMessage = "An error occurred while creating branch.");
            }
        });

        return Task.CompletedTask;
    }

    private Task CheckoutBranchAsync((int RepositoryId, string BranchName, bool IsTag) request)
    {
        var (repositoryId, branchName, isTag) = request;
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;

        errorMessage = null;

        StartPageJob(isTag ? "Checking out tag..." : "Checking out branch...", async (job, ct) =>
        {
            var (success, errMsg) = await ScopedExecutor.ExecuteAsync<WorkspaceBranchHandler, (bool Success, string? ErrorMessage)>(
                svc => svc.CheckoutBranchAsync(WorkspaceId, repositoryId, branchName, isTag, ApiBaseUrl, ct));

            SafeInvoke(() =>
            {
                if (success)
                {
                    repositoryErrors.Remove(repositoryId);
                }
                else
                {
                    var message = errMsg ?? (isTag ? "Failed to checkout tag." : "Failed to checkout branch.");
                    repositoryErrors[repositoryId] = message;
                    ToastService.ShowError(message);
                }
            });
        }, new PageJobOptions
        {
            RefreshOnCancel = true,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error checking out {Kind} for repository {RepositoryId}", isTag ? "tag" : "branch", repositoryId);
                var message = isTag
                    ? "Failed to checkout tag. The GrayMoon Agent may be offline. Start the Agent and try again."
                    : "Failed to checkout branch. The GrayMoon Agent may be offline. Start the Agent and try again.";
                SafeInvoke(() =>
                {
                    repositoryErrors[repositoryId] = message;
                    ToastService.ShowError(message);
                });
            }
        });

        return Task.CompletedTask;
    }

    private sealed record BranchModalState
    {
        public bool IsVisible { get; init; }
        public IReadOnlyList<string> CommonBranchNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> CommonLocalBranchNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> CommonRemoteBranchNames { get; init; } = Array.Empty<string>();
        public string DefaultDisplayText { get; init; } = "multiple";
        /// <summary>Local branch name when every linked repo reports the same current branch; otherwise null.</summary>
        public string? WorkspaceUnifiedCurrentBranch { get; init; }
        public string InitialTab { get; init; } = "newbranch";
    }

    private sealed record SwitchBranchModalState
    {
        public bool IsVisible { get; init; }
        public int RepositoryId { get; init; }
        public string? RepositoryName { get; init; }
        public string? CurrentBranch { get; init; }
        public string? RepositoryUrl { get; init; }
        public string? InitialTab { get; init; }
    }

    private sealed record UpdateBranchModalState
    {
        public bool IsVisible { get; init; }
        public int RepositoryId { get; init; }
        public string? RepositoryName { get; init; }
        public string? CurrentBranch { get; init; }
        public string? DefaultBranch { get; init; }
        public int CommitsBehind { get; init; }
        public string? GitHubCompareUrl { get; init; }
    }

    private void ShowUpdateBranchModal(int repositoryId)
    {
        var wr = TryGetLink(repositoryId);
        var repo = wr?.Repository;
        if (wr == null || repo == null || string.IsNullOrWhiteSpace(wr.BranchName) || wr.IsOnTag)
            return;

        var repoUrl = RepositoryUrlHelper.GetRepositoryUrl(repo.CloneUrl);
        var currentBranch = wr.BranchName;
        var defaultBranch = wr.DefaultBranchName ?? "default";
        string? compareUrl = null;
        if (!string.IsNullOrWhiteSpace(repoUrl) && !string.IsNullOrWhiteSpace(currentBranch))
        {
            var encBranch = Uri.EscapeDataString(currentBranch);
            var encDefault = Uri.EscapeDataString(defaultBranch);
            compareUrl = $"{repoUrl}/compare/{encBranch}...{encDefault}";
        }

        _updateBranchModal = _updateBranchModal with
        {
            IsVisible = true,
            RepositoryId = repositoryId,
            RepositoryName = repo.RepositoryName,
            CurrentBranch = currentBranch,
            DefaultBranch = defaultBranch,
            CommitsBehind = wr.DefaultBranchBehindCommits ?? 0,
            GitHubCompareUrl = compareUrl
        };
        StateHasChanged();
    }

    private void CloseUpdateBranchModal()
    {
        _updateBranchModal = _updateBranchModal with
        {
            IsVisible = false,
            RepositoryId = 0,
            RepositoryName = null,
            CurrentBranch = null,
            DefaultBranch = null,
            CommitsBehind = 0,
            GitHubCompareUrl = null
        };
    }

    private Task OnUpdateBranchConfirmedAsync()
    {
        var repositoryId = _updateBranchModal.RepositoryId;
        var repoName = _updateBranchModal.RepositoryName;
        if (workspace == null || repositoryId <= 0)
        {
            CloseUpdateBranchModal();
            return Task.CompletedTask;
        }

        CloseUpdateBranchModal();

        StartPageJob($"Updating branch in {repoName}...", async (job, ct) =>
        {
            var result = await ScopedExecutor.ExecuteAsync<WorkspaceBranchUpdateHandler, UpdateBranchFromDefaultResult>(
                svc => svc.UpdateBranchFromDefaultAsync(WorkspaceId, repositoryId, ct));

            if (result.HasConflicts)
            {
                var fileList = result.ConflictFiles.Count > 0
                    ? string.Join(", ", result.ConflictFiles)
                    : "unknown files";
                var conflictCount = result.ConflictFiles.Count;
                SafeInvoke(() => ToastService.ShowError(
                    $"Merge conflict in {conflictCount} {(conflictCount == 1 ? "file" : "files")}: {fileList}. " +
                    "Resolve the conflicts in your IDE, then commit."));
            }
            else if (!result.Success)
            {
                var message = result.ErrorMessage ?? "Failed to update branch. The GrayMoon Agent may be offline.";
                SafeInvoke(() => ToastService.ShowError(message));
            }
            else
            {
                SafeInvoke(() => ToastService.Show($"Branch updated successfully."));
            }
        }, new PageJobOptions
        {
            RefreshOnSuccess = false,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error updating branch from default for repository {RepositoryId}", repositoryId);
                SafeInvoke(() => ToastService.ShowError("An error occurred while updating branch."));
            }
        });

        return Task.CompletedTask;
    }
}
