using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Components.Modals;
using Microsoft.AspNetCore.Components.Web;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private UpdateSingleRepoDependenciesModalState _updateSingleRepoModal = new();
    private CustomDependenciesModalState _customDependenciesModal = new();

    private bool HasOutOfDateFiles(int repositoryId) =>
        (workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId)?.OutOfDateFileRepos ?? 0) > 0;

    private IReadOnlyList<FileVersionMismatchLine> GetMismatchedFileVersionLines(int repositoryId) =>
        _mismatchedFileVersionLinesByRepo.TryGetValue(repositoryId, out var lines) ? lines : Array.Empty<FileVersionMismatchLine>();

    private IReadOnlyList<WorkspaceFileLineStatus> GetFileLineStatus(int repositoryId) =>
        _fileLineStatusByRepo.TryGetValue(repositoryId, out var lines) ? lines : Array.Empty<WorkspaceFileLineStatus>();

    private IReadOnlyList<FileVersionDisplayLine> GetAllFileVersionLines(int repositoryId) =>
        _allFileVersionLinesByRepo.TryGetValue(repositoryId, out var lines) ? lines : Array.Empty<FileVersionDisplayLine>();

    private IReadOnlyList<DependencyMismatchLine> GetMismatchedDependencyLines(int repositoryId)
    {
        return _mismatchedDependencyLinesByRepo.GetValueOrDefault(repositoryId) ?? Array.Empty<DependencyMismatchLine>();
    }

    private IReadOnlyList<DependencyLine> GetAllDependencyLines(int repositoryId)
    {
        return _allDependencyLinesByRepo.GetValueOrDefault(repositoryId) ?? Array.Empty<DependencyLine>();
    }

    private IReadOnlyList<string> GetCustomDependencyLines(int repositoryId)
    {
        return _customDependencyLinesByRepo.GetValueOrDefault(repositoryId) ?? Array.Empty<string>();
    }

    private async Task HandleDependencyBadgeKeydown(KeyboardEventArgs e, int repositoryId, int unmatchedDeps)
    {
        if ((e.Key != "Enter" && e.Key != " ") || IsJobRunning)
            return;
        if (unmatchedDeps > 0)
            await ShowConfirmUpdateDependenciesAsync(repositoryId, unmatchedDeps);
        else if (HasOutOfDateFiles(repositoryId))
            await ShowFileVersionsCommitFlowAsync(repositoryId);
    }

    private void OnDependencyBadgeClick(int repositoryId, int unmatchedDeps)
    {
        clickedDependencyBadges.Add(repositoryId);
        _ = ShowConfirmUpdateDependenciesAsync(repositoryId, unmatchedDeps);
        StateHasChanged();
    }

    private void OnDependencyBadgeMouseLeave(int repositoryId)
    {
        if (clickedDependencyBadges.Remove(repositoryId))
        {
            StateHasChanged();
        }
    }

    private async Task ShowConfirmUpdateDependenciesAsync(int repositoryId, int unmatchedCount)
    {
        if (workspace == null || IsJobRunning)
            return;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return;
        }
        try
        {
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var workspaceGitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            var (payload, _) = await workspaceGitService.GetUpdatePlanAsync(WorkspaceId, new HashSet<int> { repositoryId });
            if (payload == null || payload.Count == 0)
            {
                if (HasOutOfDateFiles(repositoryId))
                {
                    OnFileDependencyBadgeClick(repositoryId);
                    return;
                }
                ToastService.Show("No dependency updates for this repository.");
                return;
            }
            var repoPayload = payload[0];
            var repoName = workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId)?.Repository?.RepositoryName;

            var repo = workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId);
            if (repo != null
                && !string.IsNullOrWhiteSpace(repo.DefaultBranchName)
                && string.Equals(repo.BranchName, repo.DefaultBranchName, StringComparison.Ordinal))
            {
                var hasFiles = HasOutOfDateFiles(repositoryId);
                var warningMsg = hasFiles
                    ? "The following repository is on its default branch. Updating dependencies and file versions will commit changes directly to the default (protected) branch."
                    : "The following repository is on its default branch. Updating dependencies will commit changes directly to the default (protected) branch.";
                ShowDefaultBranchWarning(
                    warningMsg,
                    new[] { new DefaultBranchWarningItem(repoName ?? $"repo {repositoryId}", repo.DefaultBranchName!) },
                    () => OpenUpdateSingleRepoModalAsync(repoPayload, repositoryId, repoName));
                return;
            }

            await OpenUpdateSingleRepoModalAsync(repoPayload, repositoryId, repoName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting update plan for repository {RepositoryId}", repositoryId);
            ToastService.Show("Could not load dependency updates.");
        }
    }

    private async Task OpenUpdateSingleRepoModalAsync(SyncDependenciesRepoPayload repoPayload, int repositoryId, string? repoName)
    {
        _updateSingleRepoModal = _updateSingleRepoModal with
        {
            IsVisible = true,
            Payload = repoPayload,
            RepositoryId = repositoryId,
            RepoName = repoName
        };
        await InvokeAsync(StateHasChanged);
    }

    private void CloseUpdateSingleRepositoryDependenciesModal()
    {
        _updateSingleRepoModal = _updateSingleRepoModal with { IsVisible = false, Payload = null };
    }

    private Task OnUpdateSingleRepositoryDependenciesProceedAsync()
    {
        if (_updateSingleRepoModal.Payload == null)
            return Task.CompletedTask;
        var repositoryId = _updateSingleRepoModal.RepositoryId;
        CloseUpdateSingleRepositoryDependenciesModal();

        repositoryErrors.Remove(repositoryId);

        StartPageJob("Updating repository...", async (job, ct) =>
        {
            await ScopedExecutor.ExecuteAsync<WorkspaceUpdateHandler>(svc =>
                svc.RunUpdateAsync(
                    WorkspaceId,
                    ct,
                    job.ReportProgress,
                    (repoId, msg) => SafeInvoke(() => { repositoryErrors[repoId] = msg; }),
                    repoIdsToUpdate: new HashSet<int> { repositoryId }));
        }, new PageJobOptions
        {
            RefreshOnCancel = true,
            OnError = ex =>
            {
                Logger.LogError(ex, "Update dependencies failed for repository {RepositoryId}", repositoryId);
                SafeInvoke(() => ToastService.Show(ex.Message));
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>Update dependencies for a single repository only (refresh projects, sync deps, no commit). Same as Update but scoped to one repo.</summary>
    private Task UpdateSingleRepositoryAsync(int repositoryId)
    {
        if (workspace == null || IsJobRunning)
            return Task.CompletedTask;
        if (IsRepoOnTag(repositoryId))
        {
            ToastService.Show(TagBlockedActionMessage);
            return Task.CompletedTask;
        }

        repositoryErrors.Remove(repositoryId);

        StartPageJob("Updating repository...", async (job, ct) =>
        {
            await ScopedExecutor.ExecuteAsync<WorkspaceGitService>(svc =>
                svc.RunUpdateSingleRepositoryAsync(
                    WorkspaceId,
                    repositoryId,
                    onProgressMessage: job.ReportProgress,
                    onRepoError: (repoId, msg) => SafeInvoke(() => { repositoryErrors[repoId] = msg; }),
                    cancellationToken: ct));
        }, new PageJobOptions
        {
            RefreshOnCancel = true,
            OnError = ex =>
            {
                Logger.LogError(ex, "Error updating repository {RepositoryId} in workspace {WorkspaceId}", repositoryId, WorkspaceId);
                SafeInvoke(() => { repositoryErrors[repositoryId] = "Update failed. The GrayMoon Agent may be offline. Start the Agent and try again."; });
            }
        });

        return Task.CompletedTask;
    }

    private async Task ShowCustomDependenciesModalAsync(int repositoryId)
    {
        if (workspaceRepositories.FirstOrDefault(wr => wr.RepositoryId == repositoryId) is not { } link)
            return;

        if (link.IsOnTag)
        {
            ToastService.Show("Repository is on a tag; checkout a branch first.");
            return;
        }

        clickedDependencyBadges.Add(repositoryId);
        StateHasChanged();

        try
        {
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var projectRepo = scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();
            var customDepRepo = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryCustomDependencyRepository>();
            var workspaceRepo = scope.ServiceProvider.GetRequiredService<WorkspaceRepository>();

            var workspaceData = await workspaceRepo.GetByIdAsync(WorkspaceId);
            var allLinks = workspaceData?.Repositories ?? workspaceRepositories;

            var implicitBySource = await projectRepo.GetImplicitReferencedRepoIdsBySourceAsync(WorkspaceId, repositoryId);
            var lockedIds = new HashSet<int>(implicitBySource.FromProject);
            lockedIds.UnionWith(implicitBySource.FromFile);
            var circularRepoIds = await projectRepo.GetCircularCustomDependencyRepoIdsAsync(WorkspaceId, repositoryId);
            var savedCustomIds = await customDepRepo.GetCustomReferencedRepositoryIdsAsync(WorkspaceId, repositoryId);

            _customDependenciesModal = new CustomDependenciesModalState
            {
                IsVisible = true,
                DependentRepositoryId = repositoryId,
                DependentWorkspaceRepositoryId = link.WorkspaceRepositoryId,
                DependentRepoName = link.Repository?.RepositoryName,
                LockedReferencedRepoIds = lockedIds,
                LockedFromProjectRepoIds = implicitBySource.FromProject,
                LockedFromFileRepoIds = implicitBySource.FromFile,
                CircularCustomDependencyRepoIds = circularRepoIds,
                SelectedCustomRepoIds = new HashSet<int>(savedCustomIds),
                Repositories = allLinks
                    .Where(wr => wr.Repository != null && !string.IsNullOrWhiteSpace(wr.Repository.RepositoryName))
                    .Where(wr => !circularRepoIds.Contains(wr.RepositoryId))
                    .Select(wr => new CustomDependenciesModal.CustomDependencyRepoEntry(
                        wr.RepositoryId,
                        wr.Repository!.RepositoryName!))
                    .OrderBy(r => r.RepositoryName, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ErrorMessage = null,
                IsSaving = false
            };
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not open custom dependencies dialog for repo {RepositoryId}", repositoryId);
            ToastService.ShowError("Could not open custom dependencies dialog.");
        }
    }

    private void CloseCustomDependenciesModal()
    {
        _customDependenciesModal = new CustomDependenciesModalState();
        StateHasChanged();
    }

    private async Task SaveCustomDependenciesAsync()
    {
        if (!_customDependenciesModal.IsVisible || _customDependenciesModal.IsSaving)
            return;

        var dependentRepoId = _customDependenciesModal.DependentRepositoryId;
        var locked = _customDependenciesModal.LockedReferencedRepoIds;
        var circular = _customDependenciesModal.CircularCustomDependencyRepoIds;
        var selected = _customDependenciesModal.SelectedCustomRepoIds
            .Where(id => !locked.Contains(id) && !circular.Contains(id) && id != dependentRepoId)
            .ToHashSet();

        _customDependenciesModal.IsSaving = true;
        _customDependenciesModal.ErrorMessage = null;
        StateHasChanged();

        try
        {
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var customDepRepo = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryCustomDependencyRepository>();
            var gitService = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

            await customDepRepo.ReplaceCustomDependenciesForDependentAsync(WorkspaceId, dependentRepoId, selected);
            await gitService.RecomputeAndBroadcastWorkspaceSyncedAsync(WorkspaceId);
            await ReloadWorkspaceDataFromFreshScopeAsync();

            CloseCustomDependenciesModal();
            ToastService.Show("Custom dependencies saved.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to save custom dependencies for repo {RepositoryId}", dependentRepoId);
            _customDependenciesModal.IsSaving = false;
            _customDependenciesModal.ErrorMessage = "Failed to save custom dependencies. Please try again.";
            StateHasChanged();
        }
    }

    private sealed record UpdateSingleRepoDependenciesModalState
    {
        public bool IsVisible { get; init; }
        public SyncDependenciesRepoPayload? Payload { get; init; }
        public int RepositoryId { get; init; }
        public string? RepoName { get; init; }
    }

    private sealed class CustomDependenciesModalState
    {
        public bool IsVisible { get; set; }
        public int DependentRepositoryId { get; set; }
        public int DependentWorkspaceRepositoryId { get; set; }
        public string? DependentRepoName { get; set; }
        public IReadOnlySet<int> LockedReferencedRepoIds { get; set; } = new HashSet<int>();
        public IReadOnlySet<int> LockedFromProjectRepoIds { get; set; } = new HashSet<int>();
        public IReadOnlySet<int> LockedFromFileRepoIds { get; set; } = new HashSet<int>();
        public IReadOnlySet<int> CircularCustomDependencyRepoIds { get; set; } = new HashSet<int>();
        public HashSet<int> SelectedCustomRepoIds { get; set; } = new();
        public IReadOnlyList<CustomDependenciesModal.CustomDependencyRepoEntry> Repositories { get; set; } = Array.Empty<CustomDependenciesModal.CustomDependencyRepoEntry>();
        public bool IsSaving { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
