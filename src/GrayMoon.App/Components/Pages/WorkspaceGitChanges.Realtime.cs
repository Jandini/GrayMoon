using System.Diagnostics;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.SignalR.Client;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    private HubConnection? _hubConnection;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && _hubConnection == null)
        {
            _hubConnection = WorkspaceSyncHubConnectionHelper.Create(NavigationManager);

            _hubConnection.On<int, int>("GitChangesUpdated", async (workspaceId, _) =>
            {
                if (workspaceId != WorkspaceId || _disposed)
                    return;

                var started = Stopwatch.GetTimestamp();
                await InvokeAsync(LoadAsync);
                LogLiveUpdateHandled("Reloaded", GitChangesBench.ElapsedMs(started), workspaceId, contextId: 0);
            });

            _hubConnection.On<int, int, int>("ContextGitChangesUpdated", async (workspaceId, contextId, _) =>
            {
                if (workspaceId != WorkspaceId || _disposed)
                    return;

                if (_selectedContextId is WorkspaceFeatureContextId selected && selected.Value != contextId)
                {
                    LogLiveUpdateHandled("NoOp", handleMs: 0, workspaceId, contextId);
                    return;
                }

                var started = Stopwatch.GetTimestamp();
                await InvokeAsync(LoadAsync);
                LogLiveUpdateHandled("Reloaded", GitChangesBench.ElapsedMs(started), workspaceId, contextId);
            });

            _ = WorkspaceSyncHubConnectionHelper.TryStartAsync(_hubConnection, Logger);
        }

        await ScrollSelectionIntoViewIfPendingAsync();
    }

    private void LogLiveUpdateHandled(string outcome, long handleMs, int workspaceId, int contextId)
    {
        // PatchedOnly arrives in Phase 1; today every accepted update reloads the full context inventory.
        var entryCount = _view?.Repositories.Sum(r => r.Changes.Count) ?? 0;
        GitChangesBench.Log(
            Logger,
            GitChangesOptions.Value,
            "live update handled",
            entryCount,
            handleMs,
            ("Outcome", outcome),
            ("HandleMs", handleMs),
            ("WorkspaceId", workspaceId),
            ("ContextId", contextId));
    }
}
