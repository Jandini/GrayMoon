using GrayMoon.App.Components.Modals;
using GrayMoon.App.Components.Shared;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private NewPullRequestModalState _newPrModal = new();

    private async Task OpenPullRequestDialogForAllRepositoriesAsync()
    {
        var links = await GetAllLinksForOperationAsync();
        await OpenPullRequestDialogCoreAsync(links);
    }

    private async Task OpenPullRequestDialogForLevelAsync(int? levelKey)
    {
        var ids = (await GetRepositoryIdsAtLevelAsync(levelKey)).ToHashSet();
        var links = (await GetAllLinksForOperationAsync()).Where(wr => ids.Contains(wr.RepositoryId)).ToList();
        await OpenPullRequestDialogCoreAsync(links);
    }

    private Task OpenPullRequestDialogForRepositoryAsync(WorkspaceRepositoryLink link)
        => OpenPullRequestDialogCoreAsync(new[] { link });

    private Task OpenPullRequestDialogForRepositoriesAsync(IReadOnlyList<WorkspaceRepositoryLink> links)
        => OpenPullRequestDialogCoreAsync(links);

    private Task OpenPullRequestDialogCoreAsync(IEnumerable<WorkspaceRepositoryLink> links)
    {
        var targets = new List<NewPrTargetRepo>();
        foreach (var wr in links)
        {
            var repo = wr.Repository;
            if (repo == null) continue;
            if (wr.IsOnTag) continue;
            if (string.IsNullOrWhiteSpace(wr.BranchName)) continue;
            if (string.IsNullOrWhiteSpace(wr.DefaultBranchName)) continue;
            if (string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal)) continue;
            if ((wr.DefaultBranchAheadCommits ?? 0) <= 0) continue;
            if (!RepositoryUrlHelper.TryParseGitHubOwnerRepo(repo.CloneUrl, out var owner, out var repoName) || owner == null || repoName == null)
                continue;

            var hasOpenPr = wr.PullRequest != null && string.Equals(wr.PullRequest.State, "open", StringComparison.OrdinalIgnoreCase);
            if (hasOpenPr) continue;

            targets.Add(new NewPrTargetRepo(
                RepositoryId: wr.RepositoryId,
                Owner: owner,
                RepositoryName: repoName,
                HeadBranch: wr.BranchName!,
                BaseBranch: wr.DefaultBranchName!,
                CloneUrl: repo.CloneUrl));
        }

        if (targets.Count == 0)
        {
            ToastService.Show("No eligible repositories to create a pull request from.");
            return Task.CompletedTask;
        }

        _newPrModal = new NewPullRequestModalState
        {
            IsVisible = true,
            Targets = targets
        };
        StateHasChanged();
        return Task.CompletedTask;
    }

    private void CloseNewPullRequestModal()
    {
        _newPrModal = _newPrModal with { IsVisible = false };
        JobService.GetJob(PageJobKey)?.Abort();
        StateHasChanged();
    }

    private async Task HandleNewPrOpenInGitHubAsync()
    {
        var targets = _newPrModal.Targets;
        if (targets.Count == 0) return;

        var urls = new List<string>();
        foreach (var t in targets)
        {
            var repoUrl = RepositoryUrlHelper.GetRepositoryUrl(t.CloneUrl);
            if (string.IsNullOrEmpty(repoUrl)) continue;
            urls.Add($"{repoUrl}/compare/{t.BaseBranch}...{Uri.EscapeDataString(t.HeadBranch)}");
        }
        if (urls.Count == 0)
        {
            ToastService.Show("No GitHub URLs are available for the selected repositories.");
            return;
        }

        async Task OpenAsync()
        {
            await JSRuntime.InvokeVoidAsync("graymoonOpenUrls", urls);
        }

        if (urls.Count > 5)
        {
            ShowConfirm($"Do you want to open {urls.Count} repositories in separate tabs?", OpenAsync);
        }
        else
        {
            await OpenAsync();
        }
    }

    private Task HandleCreatePullRequestsAsync(NewPrFormResult form)
    {
        var targets = _newPrModal.Targets;
        if (targets.Count == 0) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(form.Title))
        {
            ToastService.ShowError("Title is required.");
            return Task.CompletedTask;
        }

        var requests = targets.Select(t => new CreatePullRequestRequest
        {
            RepositoryId = t.RepositoryId,
            Owner = t.Owner,
            RepositoryName = t.RepositoryName,
            HeadBranch = t.HeadBranch,
            BaseBranch = t.BaseBranch,
            Title = form.Title,
            Body = form.Body,
            IsDraft = form.IsDraft,
            Reviewers = form.Reviewers,
            TeamReviewers = form.TeamReviewers
        }).ToList();

        var repoIdsToPush = form.RepositoryIdsToPush.ToHashSet();
        var draftSuffix = form.IsDraft ? " as draft" : string.Empty;
        var pushSuffix = repoIdsToPush.Count > 0
            ? $" (pushing {repoIdsToPush.Count} {(repoIdsToPush.Count == 1 ? "repository" : "repositories")} first)"
            : string.Empty;
        var message = requests.Count == 1
            ? $"Create pull request for {requests[0].RepositoryName}{draftSuffix}{pushSuffix}?"
            : $"Create {requests.Count} pull requests{draftSuffix}{pushSuffix}?";

        ShowConfirm(message, () => ExecuteCreatePullRequestsAsync(requests, repoIdsToPush), repoIdsToPush.Count > 0 ? "Push & Create" : "Create");
        return Task.CompletedTask;
    }

    private Task ExecuteCreatePullRequestsAsync(IReadOnlyList<CreatePullRequestRequest> requests, IReadOnlySet<int> repoIdsToPush)
    {
        if (requests.Count == 0 || IsJobRunning)
            return Task.CompletedTask;

        var total = requests.Count;
        _newPrModal = _newPrModal with { IsVisible = false };

        JobService.StartJob(PageJobKey, total == 1 ? "Creating 1 pull request..." : $"Creating {total} pull requests...", async (job, ct) =>
        {
            // Push and create run inside the same job so the loading overlay stays up continuously
            // across both phases instead of disappearing and reappearing between two separate jobs.
            if (repoIdsToPush.Count > 0)
            {
                job.ReportProgress(repoIdsToPush.Count == 1 ? "Pushing 1 repository..." : $"Pushing {repoIdsToPush.Count} repositories...");

                var pushResult = await ScopedExecutor.ExecuteAsync<IWorkspacePushOperations, OperationResult>(svc =>
                    svc.PushAsync(
                        WorkspaceId, RequireSelectedContextId(),
                        repoIdsToPush,
                        synchronizedPush: false,
                        requiredPackageIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        job.ToOperationProgress(),
                        cancellationToken: ct));

                ApplyPushResult(repoIdsToPush, pushResult);

                var failedPushRepoIds = pushResult.RepoErrors?.Keys.ToHashSet() ?? new HashSet<int>();
                if (!pushResult.Success && failedPushRepoIds.Count == 0)
                {
                    // A push-wide failure with no per-repo attribution means we can't tell which
                    // branches actually made it to the remote - bail out rather than risk creating
                    // a pull request against a branch that was never pushed.
                    SafeInvoke(() => ToastService.ShowError(pushResult.Error ?? "Push failed; no pull requests created."));
                    return;
                }

                if (failedPushRepoIds.Count > 0)
                {
                    requests = requests.Where(r => !failedPushRepoIds.Contains(r.RepositoryId)).ToList();
                    if (requests.Count == 0)
                    {
                        SafeInvoke(() => ToastService.ShowError("Push failed; no pull requests created."));
                        return;
                    }
                    total = requests.Count;
                }
            }

            var progress = new Progress<CreatePullRequestProgress>(p =>
            {
                job.ReportProgress(p.Created == 0
                    ? (p.Total == 1 ? "Creating 1 pull request..." : $"Creating {p.Total} pull requests...")
                    : $"Created {p.Created} of {p.Total} pull requests");
            });

            IReadOnlyList<CreatePullRequestResult> results;
            try
            {
                results = await PullRequestOperations.CreateAsync(requests, progress, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Create pull requests failed");
                SafeInvoke(() => ToastService.ShowError($"Failed to create pull requests: {ex.Message}"));
                throw;
            }

            var successCount = results.Count(r => r.Success);

            SafeInvoke(() =>
            {
                foreach (var failed in results.Where(r => !r.Success))
                    SetRepositoryError(failed.RepositoryId, failed.ErrorMessage ?? "Pull request creation failed.");
                foreach (var succeeded in results.Where(r => r.Success))
                    ClearRepositoryError(succeeded.RepositoryId);

                if (successCount > 0)
                    ToastService.Show($"Created {successCount} of {total} pull requests.");
            });

            var firstSuccess = results.FirstOrDefault(r => r.Success);
            if (successCount == 1 && firstSuccess?.PullRequestUrl is { Length: > 0 } url)
            {
                try
                {
                    await InvokeAsync(async () =>
                    {
                        if (_disposed) return;
                        await JSRuntime.InvokeVoidAsync("graymoonOpenUrls", new[] { url });
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to open created PR URL");
                }
            }

            var refreshedIds = results.Where(r => r.Success).Select(r => r.RepositoryId).ToList();
            if (refreshedIds.Count > 0)
            {
                try
                {
                    var freshPrs = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, IReadOnlyDictionary<int, PullRequestInfo?>>(async svc =>
                    {
                        await svc.RefreshPullRequestsAsync(WorkspaceId, refreshedIds, cancellationToken: ct);
                        return await svc.GetPersistedPullRequestsForWorkspaceAsync(WorkspaceId, ct);
                    });
                    SafeInvoke(() => { prByRepositoryId = freshPrs; });
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to refresh pull requests after creation");
                }
            }

            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                await RefreshFromSync();
            });
        });

        return Task.CompletedTask;
    }

    private sealed record NewPullRequestModalState
    {
        public bool IsVisible { get; init; }
        public IReadOnlyList<NewPrTargetRepo> Targets { get; init; } = Array.Empty<NewPrTargetRepo>();
    }

    private PullRequestInfo? GetPrInfoForRepository(int repositoryId)
    {
        return prByRepositoryId.TryGetValue(repositoryId, out var pr) ? pr : null;
    }

    private IReadOnlyList<string> GetOpenPrUrlsForGroup(IEnumerable<WorkspaceRepositoryLink> group)
    {
        var urls = new List<string>();
        foreach (var wr in group)
        {
            if (!prByRepositoryId.TryGetValue(wr.RepositoryId, out var pr) || pr == null) continue;
            if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(pr.HtmlUrl))
                urls.Add(pr.HtmlUrl);
        }
        return urls;
    }

    /// <summary>True when at least one repository in the group would show the yellow "create" PR badge.</summary>
    private bool GroupHasCreatablePr(IEnumerable<WorkspaceRepositoryLink> group)
    {
        foreach (var wr in group)
        {
            var verified = prByRepositoryId.TryGetValue(wr.RepositoryId, out var pr);
            if (PRBadge.ShowsCreateBadge(wr.IsOnTag, verified, pr, wr.DefaultBranchAheadCommits))
                return true;
        }
        return false;
    }

    /// <summary>True when at least one repository in the group has a merged pull request, meaning the default branch has moved on and "Return to Default" is worth highlighting.</summary>
    private bool GroupHasMergedPr(IEnumerable<WorkspaceRepositoryLink> group)
    {
        foreach (var wr in group)
        {
            if (prByRepositoryId.TryGetValue(wr.RepositoryId, out var pr) && pr is { IsMerged: true })
                return true;
        }
        return false;
    }

    /// <summary>True when at least one repository in the group has an open, non-conflicted pull request - "Merge PRs..." is highlighted. Cosmetic only, computed from the same render cache as GroupHasCreatablePr/GroupHasMergedPr - not the bulk-merge dialog's actual candidate source (see OpenMergePullRequestsDialogForLevelAsync).</summary>
    private bool GroupHasMergeablePr(IEnumerable<WorkspaceRepositoryLink> group)
    {
        foreach (var wr in group)
        {
            if (prByRepositoryId.TryGetValue(wr.RepositoryId, out var pr)
                && pr != null
                && string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private IReadOnlyDictionary<string, string> GetOpenPrPullMapForGroup(IEnumerable<WorkspaceRepositoryLink> group)
    {
        var map = new Dictionary<string, string>();
        foreach (var wr in group)
        {
            if (wr.Repository == null) continue;
            var baseUrl = RepositoryUrlHelper.GetRepositoryUrl(wr.Repository.CloneUrl);
            if (string.IsNullOrEmpty(baseUrl)) continue;
            if (!prByRepositoryId.TryGetValue(wr.RepositoryId, out var pr) || pr == null) continue;
            if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(pr.HtmlUrl))
                map[baseUrl] = pr.HtmlUrl;
        }
        return map;
    }
}
