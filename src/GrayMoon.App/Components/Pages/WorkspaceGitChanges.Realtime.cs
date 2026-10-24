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

            // (workspaceId, contextId, repositoryId) - repositoryId is Repository.RepositoryId.
            _hubConnection.On<int, int, int>("ContextGitChangesUpdated", async (workspaceId, contextId, repositoryId) =>
            {
                if (workspaceId != WorkspaceId || _disposed)
                    return;

                if (_selectedContextId is WorkspaceFeatureContextId selected && selected.Value != contextId)
                {
                    LogLiveUpdateNoOp(workspaceId, contextId);
                    return;
                }

                await InvokeAsync(() => PatchRepositoryLiveAsync(workspaceId, contextId, repositoryId));
            });

            _ = WorkspaceSyncHubConnectionHelper.TryStartAsync(_hubConnection, Logger);
        }

        await ScrollSelectionIntoViewIfPendingAsync();
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
