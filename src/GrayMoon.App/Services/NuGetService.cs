using System.Net.Http.Headers;
using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

public class NuGetService : IConnectorService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NuGetService> _logger;

    public ConnectorType ConnectorType => ConnectorType.NuGet;

    public NuGetService(HttpClient httpClient, ILogger<NuGetService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> TestConnectionAsync(Connector connector)
    {
        if (connector.ConnectorType != ConnectorType.NuGet)
        {
            throw new InvalidOperationException($"Connector type {connector.ConnectorType} is not supported by NuGetService.");
        }

        // NuGet.org doesn't require authentication
        if (ConnectorHelpers.IsNuGetOrg(connector.ApiBaseUrl))
        {
            return await TestNuGetOrgConnectionAsync(connector);
        }

        // Other registries require token
        if (string.IsNullOrWhiteSpace(connector.UserToken))
        {
            throw new InvalidOperationException("Connector token is required for this NuGet registry.");
        }

        return await TestAuthenticatedNuGetConnectionAsync(connector);
    }

    private async Task<bool> TestNuGetOrgConnectionAsync(Connector connector)
    {
        try
        {
            var url = connector.ApiBaseUrl.TrimEnd('/');
            if (!url.EndsWith("/v3/index.json", StringComparison.OrdinalIgnoreCase))
            {
                url = url.EndsWith("/v3", StringComparison.OrdinalIgnoreCase)
                    ? url + "/index.json"
                    : url + "/v3/index.json";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test NuGet.org connection.");
            return false;
        }
    }

    private async Task<bool> TestAuthenticatedNuGetConnectionAsync(Connector connector)
    {
        try
        {
            var url = connector.ApiBaseUrl.TrimEnd('/');
            if (!url.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase))
            {
                url = url + "/index.json";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (!string.IsNullOrWhiteSpace(connector.UserToken))
            {
                if (ConnectorHelpers.IsGitHubPackages(connector.ApiBaseUrl))
                {
                    // GitHub Packages uses Basic auth with username:token
                    var username = connector.UserName ?? "USER";
                    var credentials = Convert.ToBase64String(
                        System.Text.Encoding.ASCII.GetBytes($"{username}:{connector.UserToken}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                else if (ConnectorHelpers.IsProGet(connector.ApiBaseUrl))
                {
                    // ProGet uses API key in header or Basic auth
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connector.UserToken);
                }
            }

            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test authenticated NuGet connection.");
            return false;
        }
    }
}
