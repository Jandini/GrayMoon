using GrayMoon.Abstractions.Models;
using GrayMoon.App.Models;

namespace GrayMoon.App.Services.Connectors;

public interface IConnectorService
{
    Task<ConnectorTestResult> TestConnectionAsync(Connector connector);
    ConnectorType ConnectorType { get; }
}
