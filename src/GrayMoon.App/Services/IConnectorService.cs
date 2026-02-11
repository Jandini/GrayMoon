using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

public interface IConnectorService
{
    Task<bool> TestConnectionAsync(Connector connector);
    ConnectorType ConnectorType { get; }
}
