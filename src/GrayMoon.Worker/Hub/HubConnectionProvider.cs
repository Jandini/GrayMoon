using GrayMoon.Worker.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;

namespace GrayMoon.Worker.Hub;

public sealed class HubConnectionProvider : IHubConnectionProvider
{
    public HubConnection? Connection { get; set; }
}
