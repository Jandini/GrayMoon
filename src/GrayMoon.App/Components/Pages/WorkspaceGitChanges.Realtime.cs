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

            _hubConnection.On<int, int>("GitChangesUpdated", async (workspaceId, _) =>
            {
                if (workspaceId != WorkspaceId || _disposed)
                    return;

                await InvokeAsync(() => LoadAsync(
                    benchEventName: "live update handled",
                    liveOutcome: "Reloaded",
                    liveWorkspaceId: workspaceId,
                    liveContextId: 0));
            });

            _hubConnection.On<int, int, int>("ContextGitChangesUpdated", async (workspaceId, contextId, _) =>
            {
                if (workspaceId != WorkspaceId || _disposed)
                    return;

                if (_selectedContextId is WorkspaceFeatureContextId selected && selected.Value != contextId)
                {
                    LogLiveUpdateNoOp(workspaceId, contextId);
                    return;
                }

                await InvokeAsync(() => LoadAsync(
                    benchEventName: "live update handled",
                    liveOutcome: "Reloaded",
                    liveWorkspaceId: workspaceId,
                    liveContextId: contextId));
            });

            _ = WorkspaceSyncHubConnectionHelper.TryStartAsync(_hubConnection, Logger);
        }

        await ScrollSelectionIntoViewIfPendingAsync();
    }

    private void LogLiveUpdateNoOp(int workspaceId, int contextId)
    {
        var entryCount = _view?.Repositories.Sum(r => r.Changes.Count) ?? 0;
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
