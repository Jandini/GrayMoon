using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

public class ConnectorServiceFactory
{
    private readonly IServiceProvider _serviceProvider;

    public ConnectorServiceFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public IConnectorService GetService(ConnectorType connectorType)
    {
        return connectorType switch
        {
            ConnectorType.GitHub => _serviceProvider.GetRequiredService<GitHubService>(),
            ConnectorType.NuGet => _serviceProvider.GetRequiredService<NuGetService>(),
            _ => throw new NotSupportedException($"Connector type {connectorType} is not supported.")
        };
    }
}
