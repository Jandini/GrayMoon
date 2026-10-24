using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.SignalR.Client;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    private HubConnection? _hubConnection;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Circuit paint bench first: this is the render that followed LoadAsync's tree StateHasChanged.
        await CompletePendingBenchPaintAsync();

        if (firstRender && _hubConnection == null)
        {
            _hubConnection = WorkspaceSyncHubConnectionHelper.Create(NavigationManager);

            // Legacy dual-write companion of ContextGitChangesUpdated; ignore to avoid a second patch.
            _hubConnection.On<int, int>("GitChangesUpdated", (workspaceId, _) =>
            {
                if (workspaceId != WorkspaceId || _disposed)
                    return Task.CompletedTask;

                // ContextGitChangesUpdated is always published for the same snapshot and carries contextId.
                return Task.CompletedTask;
            });

            // (workspaceId, contextId, repositoryId, contentDigest) - repositoryId is Repository.RepositoryId.
            _hubConnection.On<int, int, int, string?>("ContextGitChangesUpdated", async (workspaceId, contextId, repositoryId, contentDigest) =>
            {
                if (workspaceId != WorkspaceId || _disposed)
                    return;

                if (_selectedContextId is WorkspaceFeatureContextId selected && selected.Value != contextId)
                {
                    LogLiveUpdateNoOp(workspaceId, contextId);
                    return;
                }

                await InvokeAsync(() =>
                {
                    // Same digest as what the page already shows: nothing to read, rebuild or re-fetch.
                    if (IsLiveUpdateDigestUnchanged(repositoryId, contentDigest))
                    {
                        LogLiveUpdateNoOp(workspaceId, contextId);
                        return Task.CompletedTask;
                    }

                    return PatchRepositoryLiveAsync(workspaceId, contextId, repositoryId);
                });
            });

            _ = WorkspaceSyncHubConnectionHelper.TryStartAsync(_hubConnection, Logger);
        }

        await ScrollSelectionIntoViewIfPendingAsync();
    }

    /// <summary>
    /// True when the broadcast digest equals the digest of the repository already in <see cref="_view"/>. A missing
    /// digest (old rows, unknown repository, no view yet) is never a no-op.
    /// </summary>
    private bool IsLiveUpdateDigestUnchanged(int repositoryId, string? contentDigest)
    {
        if (string.IsNullOrEmpty(contentDigest) || _view is null)
            return false;

        var current = _view.Repositories.FirstOrDefault(r => r.RepositoryId == repositoryId);
        return current?.ContentDigest is { Length: > 0 } known
               && string.Equals(known, contentDigest, StringComparison.Ordinal);
    }

    private void LogLiveUpdateNoOp(int workspaceId, int contextId)
    {
        var entryCount = _hydrationMode == GitChangesHydrationMode.Summary
            ? (_view?.Repositories.Sum(r => r.StagedCount + r.ChangedCount) ?? 0)
            : (_view?.Repositories.Sum(r => r.Changes.Count) ?? 0);
        GitChangesBench.Log(
            Logger,
            GitChangesOptions.Value,
            "live update handled",
            entryCount,
            elapsedMs: 0,
            ("Outcome", "NoOp"),
            ("HandleMs", 0),
            ("CircuitPaintMs", 0),
            ("WorkspaceId", workspaceId),
            ("ContextId", contextId));
    }
}
