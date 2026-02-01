using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Hubs;

public sealed class WorkspaceSyncHub : Hub
{
    // Hub exists for IHubContext<T>; clients subscribe via .On("WorkspaceSynced", ...)
}
