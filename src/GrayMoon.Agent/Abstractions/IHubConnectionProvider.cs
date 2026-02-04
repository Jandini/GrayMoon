using Microsoft.AspNetCore.SignalR.Client;

namespace GrayMoon.Agent.Abstractions;

public interface IHubConnectionProvider
{
    HubConnection? Connection { get; }
}
