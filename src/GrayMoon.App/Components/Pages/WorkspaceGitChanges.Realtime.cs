using Microsoft.AspNetCore.SignalR.Client;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    private HubConnection? _hubConnection;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && _hubConnection == null)
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(NavigationManager.ToAbsoluteUri("/hubs/workspace-sync"))
                .WithAutomaticReconnect()
                .Build();

            _hubConnection.On<int, int>("GitChangesUpdated", async (workspaceId, _) =>
            {
                if (workspaceId != WorkspaceId || _disposed)
                {
                    return;
                }

                await InvokeAsync(LoadAsync);
            });

            await _hubConnection.StartAsync();
        }

        await ScrollSelectionIntoViewIfPendingAsync();
    }
}
