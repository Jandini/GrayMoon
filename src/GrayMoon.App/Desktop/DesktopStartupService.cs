using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GrayMoon.App.Desktop;

/// <summary>
/// Sends the startup handshake to GrayMoon.Desktop through the named pipe after Kestrel has bound.
/// Only runs when Desktop:PipeName is set in configuration (i.e. --desktop was passed).
/// </summary>
public sealed class DesktopStartupService : IHostedService
{
    private readonly IServer _server;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DesktopStartupService> _logger;

    public DesktopStartupService(
        IServer server,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<DesktopStartupService> logger)
    {
        _server = server;
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var pipeName = _configuration["Desktop:PipeName"];
        if (string.IsNullOrEmpty(pipeName)) return;

        // Wait for the application to be fully started so Kestrel has bound its port
        _lifetime.ApplicationStarted.Register(async () =>
        {
            try
            {
                await SendHandshakeAsync(pipeName, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send startup handshake to GrayMoon.Desktop");
            }
        });
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SendHandshakeAsync(string pipeName, CancellationToken cancellationToken)
    {
        // Resolve the actual bound address from the server feature
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var url = addresses?.FirstOrDefault();

        if (string.IsNullOrEmpty(url))
        {
            _logger.LogError("Desktop mode: could not determine bound server address");
            return;
        }

        // Normalize the wildcard binding address that ASP.NET may report
        url = url.Replace("://[::]:", "://127.0.0.1:")
                 .Replace("://+:", "://127.0.0.1:")
                 .Replace("://0.0.0.0:", "://127.0.0.1:");

        var handshake = new
        {
            Url = url,
            Pid = Environment.ProcessId,
            Version = typeof(DesktopStartupService).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown"
        };

        _logger.LogInformation("Desktop mode: sending startup handshake {Url} to pipe {PipeName}", url, pipeName);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None);

        // Attempt to connect with a short timeout — the pipe server should already be waiting
        await client.ConnectAsync(timeout: 5000, cancellationToken);

        var json = JsonSerializer.Serialize(handshake);
        var bytes = Encoding.UTF8.GetBytes(json);
        await client.WriteAsync(bytes, cancellationToken);
        await client.FlushAsync(cancellationToken);

        _logger.LogInformation("Desktop mode: startup handshake sent successfully");
    }
}
