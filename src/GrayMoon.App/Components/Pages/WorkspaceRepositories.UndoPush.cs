using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceRepositories
{
    private UndoPushModalState _undoPushModal = new();

    /// <summary>Opens the Undo Push modal showing all repositories that have outgoing commits.</summary>
    private async Task OnUndoPushClickAsync()
    {
        if (workspace == null || IsJobRunning)
            return;

        var allLinks = await GetAllLinksForOperationAsync();
        var reposWithOutgoing = allLinks
            .Where(wr => !wr.IsOnTag && (wr.OutgoingCommits ?? 0) > 0)
            .ToList();

        if (reposWithOutgoing.Count == 0)
        {
            ToastService.Show("No outgoing commits to undo.");
            return;
        }

        _undoPushModal = _undoPushModal with
        {
            IsVisible = true,
            RepositoryLinks = reposWithOutgoing,
            Repos = reposWithOutgoing
                .Select(wr => (wr.Repository?.RepositoryName ?? $"repo {wr.RepositoryId}", wr.OutgoingCommits ?? 0))
                .ToList()
        };
        StateHasChanged();
    }

    private Task OnUndoPushProceedAsync(bool keepChanges)
    {
        if (workspace == null || IsJobRunning || _undoPushModal.RepositoryLinks.Count == 0)
            return Task.CompletedTask;

        var repoLinks = _undoPushModal.RepositoryLinks;
        CloseUndoPushModal();
        errorMessage = null;

        JobService.StartJob(PageJobKey, "Resetting outgoing commits...", async (job, ct) =>
        {
            try
            {
                await UndoPushHandler.RunUndoPushAsync(
                    WorkspaceId,
                    repoLinks,
                    keepChanges,
                    job.ReportProgress,
                    ct);

                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    await RefreshFromSync();
                });
            }
            catch (OperationCanceledException)
            {
                await ReloadWorkspaceDataAfterCancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Undo push failed for workspace {WorkspaceId}", WorkspaceId);
                SafeInvoke(() => ShowOperationError("Undo Push Failed", ex.Message));
                throw;
            }
        });

        return Task.CompletedTask;
    }

    private void CloseUndoPushModal()
    {
        _undoPushModal = _undoPushModal with
        {
            IsVisible = false,
            RepositoryLinks = Array.Empty<WorkspaceRepositoryLink>(),
            Repos = Array.Empty<(string, int)>()
        };
        StateHasChanged();
    }

    private sealed record UndoPushModalState
    {
        public bool IsVisible { get; init; }
        public IReadOnlyList<WorkspaceRepositoryLink> RepositoryLinks { get; init; } = Array.Empty<WorkspaceRepositoryLink>();
        public IReadOnlyList<(string RepoName, int OutgoingCommits)> Repos { get; init; } = Array.Empty<(string, int)>();
    }
}
