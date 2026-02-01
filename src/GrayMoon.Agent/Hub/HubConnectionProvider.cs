using Microsoft.AspNetCore.SignalR.Client;

namespace GrayMoon.Agent.Hub;

public sealed class HubConnectionProvider : IHubConnectionProvider
{
    public HubConnection? Connection { get; set; }
}
