using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Agent.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Services;

internal sealed class AgentTokenProvider(
    IOptions<AgentOptions> options,
    IWorkerSecretProvider secretProvider,
    ILogger<AgentTokenProvider> logger) : IAgentTokenProvider
{
    private readonly AgentOptions _options = options.Value;
    private readonly ILogger<AgentTokenProvider> _logger = logger;

    private readonly object _lock = new();
    private readonly Dictionary<int, string> _tokenByConnectorId = new();

    public async Task<string?> GetTokenForRepositoryAsync(int repositoryId, CancellationToken cancellationToken)
    {
        if (repositoryId <= 0)
            return null;

        var baseUrl = _options.AppApiBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogDebug("AgentTokenProvider: AppApiBaseUrl is not configured; cannot obtain token for repo {RepositoryId}.", repositoryId);
            return null;
        }

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute) };
            var path = $"/repos/{repositoryId}/connector";
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            var secret = secretProvider.GetSecret();
            if (!string.IsNullOrEmpty(secret))
                request.Headers.TryAddWithoutValidation(WorkerSecretHeader.Name, secret);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    _logger.LogWarning("AgentTokenProvider: the App rejected this Worker's secret for repo {RepositoryId}. Reinstall the Worker from GrayMoon > Worker.", repositoryId);
                else
                    _logger.LogWarning("AgentTokenProvider: GET {Path} failed for repo {RepositoryId} with status {StatusCode}.", path, repositoryId, response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<ConnectorTokenResponse>(cancellationToken: cancellationToken);
            if (payload == null || payload.ConnectorId <= 0 || string.IsNullOrWhiteSpace(payload.Token))
            {
                _logger.LogWarning("AgentTokenProvider: invalid payload for repo {RepositoryId}. ConnectorId={ConnectorId}, HasToken={HasToken}",
                    repositoryId, payload?.ConnectorId ?? 0, !string.IsNullOrWhiteSpace(payload?.Token));
                return null;
            }

            lock (_lock)
            {
                _tokenByConnectorId[payload.ConnectorId] = payload.Token!;
            }

            return payload.Token;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentTokenProvider: error obtaining token for repo {RepositoryId}.", repositoryId);
            return null;
        }
    }

    public void InvalidateByConnectorId(int connectorId)
    {
        if (connectorId <= 0)
            return;

        lock (_lock)
        {
            _tokenByConnectorId.Remove(connectorId);
        }
    }

    private sealed record ConnectorTokenResponse(int ConnectorId, string Token);
}

