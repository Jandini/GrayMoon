using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Abstractions;

public interface IWorkerTokenProvider
{
    /// <summary>
    /// Returns a connector-scoped token suitable for remote git operations for the given repository,
    /// or null when a token cannot be obtained. The Worker's implementation asks the App every time and keeps no token.
    /// </summary>
    Task<string?> GetTokenForRepositoryAsync(int repositoryId, CancellationToken cancellationToken);

    /// <summary>Clears any cached token associated with the given connector identifier.</summary>
    void InvalidateByConnectorId(int connectorId);
}

