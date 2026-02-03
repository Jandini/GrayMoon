using Microsoft.AspNetCore.SignalR.Client;

namespace GrayMoon.Agent.Hub;

public interface IHubConnectionProvider
{
    HubConnection? Connection { get; }
}
