using System.Net;
using System.Net.Sockets;
using System.Text;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using Microsoft.AspNetCore.SignalR.Client;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// A real loopback HTTP endpoint standing in for the GrayMoon App, so the capability provider is exercised
/// over an actual socket rather than through a substituted HTTP stack. There is no mocking library here, and
/// the point of these tests is partly that no request is made at all, which needs something that can count.
/// </summary>
internal sealed class FakeAppApi : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _requests = [];
    private readonly object _lock = new();

    public FakeAppApi()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>HTTP status to answer with. A non-success status is what an App-side failure looks like.</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>Body to answer with, already in the <c>RepositoryOperationCapabilities</c> wire shape.</summary>
    public string ResponseBody { get; set; } = """{"calculateRepositoryVersion":true,"discoverDotNetProjects":true}""";

    public void RespondWith(bool calculateVersion, bool discoverProjects)
    {
        StatusCode = 200;
        ResponseBody =
            $"{{\"calculateRepositoryVersion\":{(calculateVersion ? "true" : "false")}," +
            $"\"discoverDotNetProjects\":{(discoverProjects ? "true" : "false")}}}";
    }

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_lock)
                return [.. _requests];
        }
    }

    /// <summary>A port nothing is listening on, for the App-unreachable case.</summary>
    public static int FindClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var head = new StringBuilder();
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _cts.Token);
                    if (read == 0)
                        return;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                lock (_lock)
                    _requests.Add(head.ToString());

                var body = Encoding.UTF8.GetBytes(ResponseBody);
                var header =
                    $"HTTP/1.1 {StatusCode} {(StatusCode == 200 ? "OK" : "Error")}\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token);
                await stream.WriteAsync(body, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
        }
        catch
        {
            // A test that finished mid-request is not a failure.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch { /* best-effort */ }
        _cts.Dispose();
    }
}

internal sealed class FakeWorkerSecretProvider(string? secret) : IWorkerSecretProvider
{
    public string? GetSecret() => secret;
}

/// <summary>No token, which makes every hook skip the remote calls that would need one.</summary>
internal sealed class NoWorkerToken : IWorkerTokenProvider
{
    public Task<string?> GetTokenForRepositoryAsync(int repositoryId, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public void InvalidateByConnectorId(int connectorId) { }
}

/// <summary>
/// No hub connection, so a hook does all of its probing and then finds nothing to notify. That is exactly
/// what these tests need: the assertion is about which git work ran, not about the notification.
/// </summary>
internal sealed class DisconnectedHubProvider : IHubConnectionProvider
{
    public HubConnection? Connection => null;
}

/// <summary>Fixed capabilities, plus a count of how often the hooks asked for them.</summary>
internal sealed class FakeWorkspaceCapabilityProvider(RepositoryOperationCapabilities capabilities)
    : IWorkspaceCapabilityProvider
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<RepositoryOperationCapabilities> GetAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(capabilities);
    }

    public void Remember(int workspaceId, RepositoryOperationCapabilities? capabilities) { }

    public void Invalidate(int workspaceId) { }
}
