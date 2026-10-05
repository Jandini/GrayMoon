using Microsoft.AspNetCore.SignalR.Client;
using GrayMoon.Application.Features;

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
                {
                    return;
                }

                await InvokeAsync(LoadAsync);
            });

            _hubConnection.On<int, int, int>("ContextGitChangesUpdated", async (workspaceId, contextId, _) =>
            {
                if (workspaceId != WorkspaceId || _disposed)
                    return;
                if (_selectedContextId is WorkspaceFeatureContextId selected && selected.Value != contextId)
                    return;

                await InvokeAsync(LoadAsync);
            });

            await _hubConnection.StartAsync();
        }

        await ScrollSelectionIntoViewIfPendingAsync();
    }
}
