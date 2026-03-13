using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Abstractions;

public interface IAgentTokenProvider
{
    /// <summary>
    /// Returns a connector-scoped token suitable for remote git operations for the given repository,
    /// or null when a token cannot be obtained. Implementations may cache tokens in-memory.
    /// </summary>
    Task<string?> GetTokenForRepositoryAsync(int repositoryId, CancellationToken cancellationToken);

    /// <summary>Clears any cached token associated with the given connector identifier.</summary>
    void InvalidateByConnectorId(int connectorId);
}

