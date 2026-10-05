using Microsoft.AspNetCore.SignalR.Client;

namespace GrayMoon.Worker.Abstractions;

public interface IHubConnectionProvider
{
    HubConnection? Connection { get; }
}
