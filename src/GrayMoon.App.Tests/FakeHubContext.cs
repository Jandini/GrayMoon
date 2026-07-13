using Microsoft.AspNetCore.SignalR;

namespace GrayMoon.App.Tests;

/// <summary>Minimal hand-rolled IHubContext fake (no mocking library is referenced in this project) that
/// records every broadcast so tests can assert on what was sent without a real SignalR connection.</summary>
public sealed class FakeClientProxy : IClientProxy
{
    public List<(string Method, object?[] Args)> Sent { get; } = [];

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        Sent.Add((method, args));
        return Task.CompletedTask;
    }
}

public sealed class FakeHubClients : IHubClients
{
    public FakeClientProxy AllProxy { get; } = new();

    public IClientProxy All => AllProxy;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => AllProxy;
    public IClientProxy Client(string connectionId) => AllProxy;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => AllProxy;
    public IClientProxy Group(string groupName) => AllProxy;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => AllProxy;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => AllProxy;
    public IClientProxy OthersInGroup(string groupName) => AllProxy;
    public IClientProxy User(string userId) => AllProxy;
    public IClientProxy Users(IReadOnlyList<string> userIds) => AllProxy;
}

public sealed class FakeHubContext<THub> : IHubContext<THub> where THub : Hub
{
    public FakeHubClients ClientsImpl { get; } = new();

    public IHubClients Clients => ClientsImpl;
    public IGroupManager Groups => throw new NotSupportedException("Not used by these tests.");
}
