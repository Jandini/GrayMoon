using GrayMoon.App.Models;
using GrayMoon.App.Services;

namespace GrayMoon.App.Components.Pages;

/// <summary>
/// Merge confirmation dialog opened from the "merge" segment of the open-PR chip. Every piece of mergeability
/// data shown here (and the merge action itself) is fetched fresh from GitHub via WorkspacePullRequestService -
/// never derived from the polled/cached PR badge state - so GitHub remains the sole authority on whether and how
/// the merge can proceed.
/// </summary>
public sealed partial class WorkspaceRepositories
{
    private MergePullRequestModalState _mergePrModal = new();

    private async Task OpenMergeDialogAsync(WorkspaceRepositoryLink link)
    {
        var prNumber = GetPrInfoForRepository(link.RepositoryId)?.Number;
        if (prNumber is not > 0)
            return;

        _mergePrModal = new MergePullRequestModalState
        {
            IsVisible = true,
            RepositoryId = link.RepositoryId,
            PrNumber = prNumber.Value,
            IsLoading = true
        };
        StateHasChanged();

        PullRequestMergeDetails? details = null;
        try
        {
            details = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, PullRequestMergeDetails?>(
                svc => svc.GetMergeDetailsAsync(WorkspaceId, link.RepositoryId, prNumber.Value));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load merge details for repo {RepositoryId}, PR #{PrNumber}", link.RepositoryId, prNumber.Value);
        }

        if (_disposed || !_mergePrModal.IsVisible || _mergePrModal.RepositoryId != link.RepositoryId)
            return;

        _mergePrModal = _mergePrModal with
        {
            IsLoading = false,
            Details = details,
            ErrorMessage = details == null ? "Could not load pull request details from GitHub." : null
        };
        StateHasChanged();
    }

    private void CloseMergeModal()
    {
        _mergePrModal = _mergePrModal with { IsVisible = false };
        StateHasChanged();
    }

    private async Task HandleMergeConfirmedAsync(MergeMethod method)
    {
        var repositoryId = _mergePrModal.RepositoryId;
        var prNumber = _mergePrModal.PrNumber;
        var headSha = _mergePrModal.Details?.HeadSha;
        if (repositoryId <= 0 || prNumber <= 0 || _mergePrModal.IsMerging)
            return;

        _mergePrModal = _mergePrModal with { IsMerging = true, ErrorMessage = null };
        StateHasChanged();

        MergeResult result;
        try
        {
            result = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, MergeResult>(
                svc => svc.MergePullRequestAsync(WorkspaceId, repositoryId, prNumber, method, headSha));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Merge PR failed for repo {RepositoryId}, PR #{PrNumber}", repositoryId, prNumber);
            result = new MergeResult(false, ex.Message);
        }

        if (_disposed)
            return;

        if (result.Success)
        {
            _mergePrModal = new MergePullRequestModalState();
            ToastService.Show($"Merged pull request #{prNumber}.");
            await RefreshFromSync();
        }
        else
        {
            _mergePrModal = _mergePrModal with { IsMerging = false, ErrorMessage = result.Message ?? "The merge could not be completed." };
        }
        StateHasChanged();
    }

    private sealed record MergePullRequestModalState
    {
        public bool IsVisible { get; init; }
        public int RepositoryId { get; init; }
        public int PrNumber { get; init; }
        public bool IsLoading { get; init; }
        public bool IsMerging { get; init; }
        public PullRequestMergeDetails? Details { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
